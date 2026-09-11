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

	"github.com/Alia5/VIIPER/device"
	vilog "github.com/Alia5/VIIPER/internal/log"
	usbserver "github.com/Alia5/VIIPER/internal/server/usb"
	"github.com/Alia5/VIIPER/virtualbus"
)

// Each backend owns one attachment and HID interface. Only device creation and
// removal share the native lifecycle lock; live input remains independent.
// Recorded 2026-09-11: real owned HID neutral startup/reset/removal and mixed
// 2 Xbox + 2 DualSense button/axis isolation; see docs/multi-controller-acceptance.md.
const dualSenseRuntimeAcceptanceRecorded = true

type dualSenseUSBBackend struct {
	mu                sync.Mutex
	pad               *dualSensePad
	server            *usbserver.Server
	bus               *virtualbus.VirtualBus
	attachment        *nativeAttachment
	observer          *ownedHIDInput
	identity          ownedHIDIdentity
	closed, connected bool
	neutralOnly       bool
	connectCancel     context.CancelFunc
	connectDone       chan struct{}
	serverStopped     chan struct{}
	serverError       error
	cleanupOnce       sync.Once
	cleanupDone       chan struct{}
	cleanupError      error
}

func newDualSenseProbeBackend() outputBackend {
	return &dualSenseUSBBackend{neutralOnly: true, cleanupDone: make(chan struct{})}
}

func (b *dualSenseUSBBackend) Connect(ctx context.Context) (result error) {
	if !nativeABIProbeCandidateReviewed || (!b.neutralOnly && !dualSenseRuntimeAcceptanceRecorded) {
		return errors.New(dualSenseAcceptanceRequired)
	}
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	b.mu.Lock()
	if b.closed || b.connectDone != nil {
		b.mu.Unlock()
		return errors.New("DualSense backend closed or already started")
	}
	b.connectCancel = cancel
	b.connectDone = make(chan struct{})
	done := b.connectDone
	b.mu.Unlock()
	defer close(done)
	if err := ctx.Err(); err != nil {
		return err
	}
	release, err := acquireNativeLifecycle(ctx)
	if err != nil {
		return err
	}
	defer func() {
		b.mu.Lock()
		a := b.attachment
		b.mu.Unlock()
		result = errors.Join(result, finishNativeLifecycle(release, a))
	}()
	serial, err := newNativeSerial()
	if err != nil {
		return err
	}
	p, err := newDualSensePadWithSerial(serial)
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
		_ = server.RemoveBus(busID)
		_ = server.Close()
		return errors.New("DualSense closed during preparation")
	}
	b.pad = p
	b.bus = bus
	b.server = server
	b.serverStopped = make(chan struct{})
	serverStopped := b.serverStopped
	b.mu.Unlock()
	go func() {
		err := server.ListenAndServe()
		b.mu.Lock()
		b.serverError = err
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
		return errors.Join(errors.New("private DualSense USB listener failed"), err)
	case <-server.Ready():
	}
	meta := device.GetDeviceMeta(devCtx)
	if meta == nil {
		return errors.New("own DualSense device metadata missing")
	}
	a, err := attachNativeWithSerial(ctx, "127.0.0.1", server.GetListenPort(), fmt.Sprintf("%d-%d", meta.BusID, meta.DevID), serial, false)
	b.mu.Lock()
	b.attachment = a
	if a != nil {
		if source, ok := a.transport.(interface{ ControllerIdentity() string }); ok {
			// Retain the expected identity even if attach was canceled midway, so
			// cleanup can detect a later own HID appearance without guessing.
			b.identity = ownedHIDIdentity{ControllerID: source.ControllerIdentity(), Serial: a.ownership.Serial, Port: a.ownership.Port}
		}
	}
	closed := b.closed
	b.mu.Unlock()
	if err != nil {
		return err
	}
	if closed {
		return errors.New("DualSense closed during attach")
	}
	var candidate ownedHIDCandidate
	for {
		identity, err := a.HIDIdentity(ctx)
		if err != nil {
			return err
		}
		b.mu.Lock()
		expected := b.identity
		b.mu.Unlock()
		if identity != expected {
			return errors.New("DualSense attachment identity changed")
		}
		items, err := enumerateOwnedHID(ctx, identity)
		if err != nil {
			return err
		}
		if len(items) > 1 {
			return errors.New("multiple HID interfaces claim the exact own DualSense identity")
		}
		if len(items) == 1 {
			candidate = items[0]
			break
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(5 * time.Millisecond):
		}
	}
	observer, err := openOwnedHID(ctx, candidate)
	b.mu.Lock()
	b.observer = observer
	closed = b.closed
	b.mu.Unlock()
	if err != nil {
		return err
	}
	if closed {
		return errors.New("DualSense closed during HID observation setup")
	}
	// Reading starts at the newly opened exact HID interface. A single observed
	// nonneutral startup report is a permanent failure, even if later neutral.
	first := time.Time{}
	samples := 0
	for {
		report, err := observer.Read(ctx)
		if err != nil {
			return err
		}
		if !neutralDualSenseReport(report) {
			return errors.New("nonneutral DualSense HID startup report rejected")
		}
		if first.IsZero() {
			first = time.Now()
		}
		samples++
		if samples >= 5 && time.Since(first) >= 50*time.Millisecond {
			break
		}
	}
	if _, err = a.HIDIdentity(ctx); err != nil {
		return err
	}
	if err = requireOwnedHID(ctx, candidate); err != nil {
		return err
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	if err = ctx.Err(); err != nil {
		return err
	}
	if b.closed {
		return errors.New("DualSense closed before CONNECT confirmation")
	}
	b.connected = true
	return nil
}

func (b *dualSenseUSBBackend) Submit(ctx context.Context, value packet) error {
	if b.neutralOnly && value != (packet{}) {
		return errors.New("explicit DualSense acceptance probe permits neutral frames only")
	}
	b.mu.Lock()
	a, observer := b.attachment, b.observer
	valid := b.connected && !b.closed
	b.mu.Unlock()
	if !valid || a == nil || observer == nil {
		return errors.New("DualSense not connected")
	}
	identity, err := a.HIDIdentity(ctx)
	if err != nil {
		return err
	}
	if identity != observer.candidate.Identity {
		return errors.New("DualSense attachment ownership changed")
	}
	// The open observer remains bound to the exact interface proven at startup.
	// Full native ownership is checked above on every frame; enumerating every
	// Windows HID device here would add work proportional to unrelated hardware.
	b.mu.Lock()
	defer b.mu.Unlock()
	if err = ctx.Err(); err != nil {
		return err
	}
	if !b.connected || b.closed {
		return errors.New("DualSense closed before frame update")
	}
	return b.pad.setPacket(value)
}

func (b *dualSenseUSBBackend) Neutral(ctx context.Context) error {
	b.mu.Lock()
	p, a, observer := b.pad, b.attachment, b.observer
	var before uint32
	var err error
	if p != nil {
		before, err = p.setPacketAfter(packet{})
	}
	b.mu.Unlock()
	if err != nil {
		return err
	}
	if a == nil {
		return nil
	}
	if observer == nil {
		return errors.New("DualSense was attached but no HID neutral readback was established")
	}
	identity, err := a.HIDIdentity(ctx)
	if err != nil {
		return err
	}
	if identity != observer.candidate.Identity {
		return errors.New("DualSense neutralization ownership changed")
	}
	if err = requireOwnedHID(ctx, observer.candidate); err != nil {
		return err
	}
	for {
		report, err := observer.Read(ctx)
		if err != nil {
			return err
		}
		if !dualSenseReportAfter(report, before) {
			continue
		}
		if !neutralDualSenseReport(report) {
			return errors.New("fresh DualSense HID report is not neutral after reset")
		}
		return nil
	}
}

func (b *dualSenseUSBBackend) Close(ctx context.Context) error {
	b.mu.Lock()
	b.closed = true
	b.connected = false
	if b.connectCancel != nil {
		b.connectCancel()
	}
	if b.pad != nil {
		_ = b.pad.setPacket(packet{})
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

func (b *dualSenseUSBBackend) closeResources(ctx context.Context) (result error) {
	b.mu.Lock()
	pending := b.connectDone
	b.mu.Unlock()
	if pending != nil {
		select {
		case <-pending:
		case <-ctx.Done():
			return errors.Join(errors.New("pending DualSense ownership not settled"), ctx.Err())
		}
	}
	b.mu.Lock()
	observer, a, server, bus, stopped, identity := b.observer, b.attachment, b.server, b.bus, b.serverStopped, b.identity
	b.mu.Unlock()
	// QUIT already requests Neutral first, but Close also checks a fresh neutral
	// report when called directly. A failed proof never prevents detach cleanup.
	if a != nil {
		neutralCtx, cancel := context.WithTimeout(ctx, 30*time.Millisecond)
		result = errors.Join(result, b.Neutral(neutralCtx))
		cancel()
	}
	release, err := acquireNativeLifecycle(ctx)
	if err != nil {
		return errors.Join(result, err)
	}
	defer func() { result = errors.Join(result, finishNativeLifecycle(release, a)) }()
	if observer != nil {
		// Begin cancellation, but do not spend the detach deadline waiting for
		// HID completion. Device removal itself may release the pending read.
		observer.beginClose()
	}
	if a != nil {
		result = errors.Join(result, a.Close(ctx))
	}
	if observer != nil {
		result = errors.Join(result, observer.Close(ctx))
	}
	if server != nil && stopped != nil {
		select {
		case <-server.Ready():
		case <-stopped:
		case <-ctx.Done():
			return errors.Join(result, errors.New("DualSense listener startup not settled"), ctx.Err())
		}
	}
	if server != nil && bus != nil {
		result = errors.Join(result, server.RemoveBus(bus.BusID()))
	}
	if server != nil {
		result = errors.Join(result, server.Close())
	}
	if a != nil {
		if identity.ControllerID == "" || !validNativeSerial(identity.Serial) {
			return errors.Join(result, errors.New("cannot prove HID removal without exact attachment ancestry"))
		}
		result = errors.Join(result, waitOwnedHIDRemoved(ctx, identity))
	}
	return result
}
