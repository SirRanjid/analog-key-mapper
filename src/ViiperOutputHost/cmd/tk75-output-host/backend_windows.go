//go:build windows && amd64

package main

import (
	"context"
	"crypto/rand"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"sync"
	"time"
	"unsafe"

	"github.com/Alia5/VIIPER/device"
	"github.com/Alia5/VIIPER/device/xbox360"
	vilog "github.com/Alia5/VIIPER/internal/log"
	usbserver "github.com/Alia5/VIIPER/internal/server/usb"
	"github.com/Alia5/VIIPER/usbip"
	"github.com/Alia5/VIIPER/virtualbus"
	"golang.org/x/sys/windows"
)

// This wrapper only observes the report requested by the USB transport. It is
// additional evidence; CONNECT also requires a neutral XInput readback.
type trackedPad struct {
	*xbox360.Xbox360
	firstNeutral chan struct{}
	seen         sync.Once
}

func newTrackedPad() (*trackedPad, error) {
	p, err := xbox360.New(nil)
	if err != nil {
		return nil, err
	}
	p.UpdateInputState(xbox360.InputState{})
	return &trackedPad{Xbox360: p, firstNeutral: make(chan struct{})}, nil
}

func (p *trackedPad) HandleTransfer(ep, dir uint32, out []byte) []byte {
	report := p.Xbox360.HandleTransfer(ep, dir, out)
	if ep == 1 && dir == usbip.DirIn && len(report) == 20 && report[0] == 0 && report[1] == 20 {
		zero := true
		for _, v := range report[2:] {
			if v != 0 {
				zero = false
				break
			}
		}
		if zero {
			p.seen.Do(func() { close(p.firstNeutral) })
		}
	}
	return report
}

type usbBackend struct {
	mu                 sync.Mutex
	pad                *trackedPad
	server             *usbserver.Server
	bus                *virtualbus.VirtualBus
	attachment         *nativeAttachment
	slot               int
	closed, connected  bool
	neutralOnly        bool
	exclusiveOwnership bool
	connectCancel      context.CancelFunc
	connectDone        chan struct{}
	serverStopped      chan struct{}
	serverError        error
	cleanupOnce        sync.Once
	cleanupDone        chan struct{}
	cleanupError       error
}

func newUSBBackend() outputBackend {
	return &unavailableOutput{reason: "Xbox USB/IP production output requires a completed real startup-neutrality and cleanup acceptance. No virtual device was started."}
}

func newXboxProbeBackend() outputBackend {
	return &usbBackend{slot: -1, neutralOnly: true, exclusiveOwnership: true, cleanupDone: make(chan struct{})}
}

// The installed driver's neutral start and own removal passed the explicit
// 2026-09-11 local probe (captures/viiper-neutral-after-reboot-20260911-001153.json,
// SHA256 1175109F6BCD793E6C67D9726B99568E65ED174AADCC9A4EE3310501874854C2).
// This opens only an explicit experimental single-controller mode. It is not
// full ABI verification, multi-controller approval, or crash-cleanup proof.
const xboxRuntimeAcceptanceRecorded = true

func newXboxRuntimeBackend() outputBackend {
	return &usbBackend{slot: -1, exclusiveOwnership: true, cleanupDone: make(chan struct{})}
}

func (b *usbBackend) Connect(ctx context.Context) (result error) {
	// Runtime permits real input but retains every exclusive ownership check.
	if !xboxAttachmentAllowed(b.neutralOnly, b.exclusiveOwnership, nativeABIProbeCandidateReviewed, xboxRuntimeAcceptanceRecorded) {
		return errors.New("USB/IP Xbox output requires reviewed exclusive ownership and recorded runtime acceptance")
	}
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	b.mu.Lock()
	if b.closed || b.connectDone != nil {
		b.mu.Unlock()
		return errors.New("backend closed or already started")
	}
	b.connectCancel = cancel
	b.connectDone = make(chan struct{})
	done := b.connectDone
	b.mu.Unlock()
	// Close waits until every partial attachment has been published. Rollback is
	// owned by the session; calling Close inside this method would self-deadlock.
	defer close(done)
	baseline, err := readXInputSlots()
	if err != nil {
		return err
	}
	free := 0
	for _, s := range baseline {
		if !s.present {
			free++
		}
	}
	if free == 0 {
		return errors.New("all four XInput slots are occupied")
	}
	p, err := newTrackedPad()
	if err != nil {
		return err
	}
	var random [4]byte
	if _, err = rand.Read(random[:]); err != nil {
		return err
	}
	busID := binary.LittleEndian.Uint32(random[:])
	if busID == 0 {
		busID = 1
	}
	bus, err := virtualbus.NewWithBusID(busID)
	if err != nil {
		return err
	}
	devCtx, err := bus.Add(p)
	if err != nil {
		bus.Close()
		return err
	}
	if timer := device.GetConnTimer(devCtx); timer != nil {
		timer.Stop()
	}
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	server := usbserver.New(usbserver.ServerConfig{Addr: "127.0.0.1:0", ConnectionTimeout: time.Second, BusCleanupTimeout: time.Second, WriteBatchFlushInterval: 0}, logger, vilog.NewRaw(nil))
	if err = server.AddBus(bus); err != nil {
		bus.Close()
		return err
	}
	b.mu.Lock()
	if b.closed {
		b.mu.Unlock()
		bus.Close()
		return errors.New("backend closed during preparation")
	}
	b.pad = p
	b.bus = bus
	b.server = server
	b.serverStopped = make(chan struct{})
	serverStopped := b.serverStopped
	b.mu.Unlock()
	go func() {
		listenErr := server.ListenAndServe()
		b.mu.Lock()
		b.serverError = listenErr
		b.mu.Unlock()
		close(serverStopped)
	}()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-serverStopped:
		b.mu.Lock()
		err = b.serverError
		b.mu.Unlock()
		return errors.Join(errors.New("USB listener failed"), err)
	case <-server.Ready():
	}
	meta := device.GetDeviceMeta(devCtx)
	if meta == nil {
		return errors.New("own device metadata missing")
	}
	a, err := attachNative(ctx, "127.0.0.1", server.GetListenPort(), fmt.Sprintf("%d-%d", meta.BusID, meta.DevID), b.exclusiveOwnership)
	b.mu.Lock()
	b.attachment = a
	closed := b.closed
	b.mu.Unlock()
	if err != nil {
		return err
	}
	if closed {
		return errors.New("backend closed during attach")
	}
	// No nonzero state is ever submitted during enumeration. Only a single new
	// previously free slot is acceptable; concurrent topology changes fail closed.
	slot := -1
	var neutralSince time.Time
	for {
		states, err := readXInputSlots()
		if err != nil {
			return err
		}
		candidate := -1
		for i := range states {
			if baseline[i].present && !states[i].present {
				return errors.New("XInput topology changed during CONNECT")
			}
			if !baseline[i].present && states[i].present {
				if candidate != -1 {
					return errors.New("multiple new XInput slots during CONNECT")
				}
				candidate = i
				b.mu.Lock()
				b.slot = candidate
				b.mu.Unlock()
				if states[i].value != (packet{}) {
					return errors.New("nonneutral XInput startup state rejected")
				}
			}
		}
		if candidate >= 0 {
			if slot >= 0 && slot != candidate {
				return errors.New("XInput slot changed during CONNECT")
			}
			slot = candidate
			select {
			case <-p.firstNeutral:
				if neutralSince.IsZero() {
					neutralSince = time.Now()
				}
			default:
			}
			if !neutralSince.IsZero() && time.Since(neutralSince) >= 50*time.Millisecond {
				break
			}
		} else {
			neutralSince = time.Time{}
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(5 * time.Millisecond):
		}
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	if err := ctx.Err(); err != nil {
		return err
	}
	if b.closed {
		return errors.New("backend closed before CONNECT confirmation")
	}
	b.slot = slot
	b.connected = true
	return nil
}

func (b *usbBackend) Submit(ctx context.Context, p packet) error {
	if b.neutralOnly && p != (packet{}) {
		return errors.New("Xbox acceptance probe rejects nonneutral input")
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	b.mu.Lock()
	a := b.attachment
	valid := !b.closed && b.connected && b.pad != nil
	b.mu.Unlock()
	if !valid || a == nil {
		return errors.New("controller not connected")
	}
	if err := a.Alive(ctx); err != nil {
		return err
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	if err := ctx.Err(); err != nil {
		return err
	}
	if b.closed || !b.connected || b.pad == nil {
		return errors.New("controller not connected")
	}
	b.pad.UpdateInputState(xbox360.InputState{Buttons: uint32(p.Buttons), LT: p.LT, RT: p.RT, LX: p.LX, LY: p.LY, RX: p.RX, RY: p.RY})
	return nil
}

func (b *usbBackend) Neutral(ctx context.Context) error {
	b.mu.Lock()
	p, slot := b.pad, b.slot
	if p != nil {
		p.UpdateInputState(xbox360.InputState{})
	}
	b.mu.Unlock()
	if p == nil || slot < 0 {
		return nil
	}
	for {
		states, err := readXInputSlots()
		if err != nil {
			return err
		}
		if !states[slot].present || states[slot].value == (packet{}) {
			return nil
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(5 * time.Millisecond):
		}
	}
}

func (b *usbBackend) Close(ctx context.Context) error {
	b.mu.Lock()
	b.closed = true
	b.connected = false
	if b.connectCancel != nil {
		b.connectCancel()
	}
	if b.pad != nil {
		b.pad.UpdateInputState(xbox360.InputState{})
	}
	b.mu.Unlock()
	b.cleanupOnce.Do(func() { go func() { b.cleanupError = b.closeResources(ctx); close(b.cleanupDone) }() })
	select {
	case <-b.cleanupDone:
		return b.cleanupError
	case <-ctx.Done():
		return ctx.Err()
	}
}

func (b *usbBackend) closeResources(ctx context.Context) error {
	b.mu.Lock()
	connectDone := b.connectDone
	b.mu.Unlock()
	if connectDone != nil {
		select {
		case <-connectDone:
		case <-ctx.Done():
			return errors.Join(errors.New("pending CONNECT ownership was not settled"), ctx.Err())
		}
	}
	b.mu.Lock()
	a, server, bus, slot, serverStopped := b.attachment, b.server, b.bus, b.slot, b.serverStopped
	b.mu.Unlock()
	var result error
	// Native Close owns exactly one location/positive hub port. It must not
	// report success for an unconfirmed timeout or detach any unrelated port.
	if a != nil {
		result = errors.Join(result, a.Close(ctx))
	}
	// A cancellation may race ListenAndServe before it has bound its socket.
	// Never acknowledge cleanup until it has either started or already failed.
	if server != nil && serverStopped != nil {
		select {
		case <-server.Ready():
		case <-serverStopped:
		case <-ctx.Done():
			return errors.Join(result, errors.New("USB listener startup was not settled"), ctx.Err())
		}
	}
	if server != nil && bus != nil {
		result = errors.Join(result, server.RemoveBus(bus.BusID()))
	}
	if server != nil {
		result = errors.Join(result, server.Close())
	}
	if slot >= 0 {
		for {
			states, err := readXInputSlots()
			if err != nil {
				return errors.Join(result, err)
			}
			if !states[slot].present {
				break
			}
			select {
			case <-ctx.Done():
				return errors.Join(result, errors.New("owned XInput slot removal not confirmed"), ctx.Err())
			case <-time.After(5 * time.Millisecond):
			}
		}
	}
	return result
}

type observedSlot struct {
	present bool
	value   packet
}
type xinputState struct {
	Number         uint32
	Buttons        uint16
	LT, RT         uint8
	LX, LY, RX, RY int16
}

var xinputGetState = windows.NewLazySystemDLL("xinput1_4.dll").NewProc("XInputGetState")

func readXInputSlots() ([4]observedSlot, error) {
	var result [4]observedSlot
	if err := xinputGetState.Find(); err != nil {
		return result, err
	}
	for i := range result {
		var state xinputState
		code, _, _ := xinputGetState.Call(uintptr(i), uintptr(unsafe.Pointer(&state)))
		if code == 1167 {
			continue
		}
		if code != 0 {
			return result, fmt.Errorf("XInputGetState failed: %d", code)
		}
		result[i] = observedSlot{true, packet{state.Buttons, state.LT, state.RT, state.LX, state.LY, state.RX, state.RY}}
	}
	return result, nil
}
