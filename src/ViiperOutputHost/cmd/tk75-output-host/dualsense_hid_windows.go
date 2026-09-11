//go:build windows && amd64

package main

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"runtime"
	"strings"
	"sync"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// The driver supplies the attachment's requested serial as its USB serial
// descriptor: usbip-win2 83bd1f7 device_ioctl.cpp:fill_usb_device_serial and
// wsk_receive.cpp:post_control_transfer. This bridges native ownership to PnP.
// SetupAPI gives the HID devnode; CM_Get_Parent gives its exact ancestry.
type hidDevInfo struct {
	Size      uint32
	ClassGUID windows.GUID
	DevInst   uint32
	Reserved  uintptr
}
type hidInterfaceInfo struct {
	Size      uint32
	ClassGUID windows.GUID
	Flags     uint32
	Reserved  uintptr
}
type ownedHIDCandidate struct {
	Path, InstanceID string
	Identity         ownedHIDIdentity
}

var ownHIDGUID = windows.GUID{Data1: 0x4D1E55B2, Data2: 0xF16F, Data3: 0x11CF, Data4: [8]byte{0x88, 0xCB, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30}}
var ownCM = windows.NewLazySystemDLL("cfgmgr32.dll")
var ownCMID = ownCM.NewProc("CM_Get_Device_IDW")
var ownCMParent = ownCM.NewProc("CM_Get_Parent")
var ownHID = windows.NewLazySystemDLL("hid.dll")
var ownHIDSerial = ownHID.NewProc("HidD_GetSerialNumberString")
var ownHIDAttributes = ownHID.NewProc("HidD_GetAttributes")
var ownHIDPreparsed = ownHID.NewProc("HidD_GetPreparsedData")
var ownHIDFreePreparsed = ownHID.NewProc("HidD_FreePreparsedData")
var ownHIDCaps = ownHID.NewProc("HidP_GetCaps")

func hidDevNodeID(node uint32) (string, error) {
	var value [512]uint16
	code, _, _ := ownCMID.Call(uintptr(node), uintptr(unsafe.Pointer(&value[0])), uintptr(len(value)), 0)
	if code != 0 {
		return "", fmt.Errorf("CM_Get_Device_ID failed: 0x%X", code)
	}
	return windows.UTF16ToString(value[:]), nil
}

func hidAncestors(ctx context.Context, node uint32) ([]string, error) {
	chain := make([]string, 0, 8)
	seen := map[uint32]bool{}
	for i := 0; i < 32; i++ {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		if seen[node] {
			return nil, errors.New("cyclic PnP ancestry")
		}
		seen[node] = true
		id, err := hidDevNodeID(node)
		if err != nil {
			return nil, err
		}
		chain = append(chain, id)
		var parent uint32
		code, _, _ := ownCMParent.Call(uintptr(unsafe.Pointer(&parent)), uintptr(node), 0)
		if code == uintptr(windows.CR_NO_SUCH_DEVNODE) {
			// The same error can mean a node vanished during enumeration. Only
			// the known PnP root is a successful end; uncertainty is not absence.
			if strings.EqualFold(id, `HTREE\ROOT\0`) {
				return chain, nil
			}
			return nil, errors.New("PnP ancestry changed before reaching its root")
		}
		if code != 0 {
			return nil, fmt.Errorf("CM_Get_Parent failed: 0x%X", code)
		}
		node = parent
	}
	return nil, errors.New("PnP ancestry exceeds bounded depth")
}

func enumerateOwnedHID(ctx context.Context, expected ownedHIDIdentity) ([]ownedHIDCandidate, error) {
	type result struct {
		items []ownedHIDCandidate
		err   error
	}
	done := make(chan result, 1)
	go func() { items, err := collectOwnedHID(ctx, expected); done <- result{items, err} }()
	select {
	case r := <-done:
		return r.items, r.err
	case <-ctx.Done():
		return nil, ctx.Err()
	}
}

func collectOwnedHID(ctx context.Context, expected ownedHIDIdentity) ([]ownedHIDCandidate, error) {
	if expected.ControllerID == "" || !validNativeSerial(expected.Serial) {
		return nil, errors.New("incomplete expected HID ownership")
	}
	set, _, e := nativeGetClass.Call(uintptr(unsafe.Pointer(&ownHIDGUID)), 0, 0, 0x12)
	if windows.Handle(set) == windows.InvalidHandle {
		return nil, fmt.Errorf("HID discovery: %w", e)
	}
	defer nativeDestroy.Call(set)
	var found []ownedHIDCandidate
	for index := uint32(0); index < 1024; index++ {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		item := hidInterfaceInfo{Size: uint32(unsafe.Sizeof(hidInterfaceInfo{}))}
		ok, _, e := nativeEnum.Call(set, 0, uintptr(unsafe.Pointer(&ownHIDGUID)), uintptr(index), uintptr(unsafe.Pointer(&item)))
		if ok == 0 {
			if e == windows.ERROR_NO_MORE_ITEMS {
				return found, nil
			}
			return nil, e
		}
		var needed uint32
		ok, _, e = nativeDetail.Call(set, uintptr(unsafe.Pointer(&item)), 0, 0, uintptr(unsafe.Pointer(&needed)), 0)
		if ok == 0 && e != windows.ERROR_INSUFFICIENT_BUFFER {
			return nil, e
		}
		if needed < 6 || needed > 65536 || needed%2 != 0 {
			return nil, errors.New("invalid HID interface detail length")
		}
		buffer := make([]byte, needed)
		binary.LittleEndian.PutUint32(buffer, 8)
		dev := hidDevInfo{Size: uint32(unsafe.Sizeof(hidDevInfo{}))}
		ok, _, e = nativeDetail.Call(set, uintptr(unsafe.Pointer(&item)), uintptr(unsafe.Pointer(&buffer[0])), uintptr(needed), 0, uintptr(unsafe.Pointer(&dev)))
		if ok == 0 {
			return nil, e
		}
		chain, err := hidAncestors(ctx, dev.DevInst)
		if err != nil {
			return nil, err
		} // unknown is never proof of removal
		if !expected.matchesAncestors(chain) {
			continue
		}
		chars := make([]uint16, (int(needed)-4)/2)
		for i := range chars {
			chars[i] = binary.LittleEndian.Uint16(buffer[4+2*i:])
		}
		if len(chars) == 0 || chars[len(chars)-1] != 0 {
			return nil, errors.New("unterminated HID interface path")
		}
		path := windows.UTF16ToString(chars)
		if path == "" {
			return nil, errors.New("empty owned HID path")
		}
		found = append(found, ownedHIDCandidate{Path: path, InstanceID: chain[0], Identity: expected})
	}
	return nil, errors.New("HID interface count exceeds bounded enumeration")
}

func requireOwnedHID(ctx context.Context, candidate ownedHIDCandidate) error {
	items, err := enumerateOwnedHID(ctx, candidate.Identity)
	if err != nil {
		return err
	}
	if len(items) != 1 || !strings.EqualFold(items[0].Path, candidate.Path) || !strings.EqualFold(items[0].InstanceID, candidate.InstanceID) {
		return errors.New("owned HID interface identity changed")
	}
	return nil
}

type ownedHIDInput struct {
	handle     windows.Handle
	candidate  ownedHIDCandidate
	gate       chan struct{}
	mu         sync.Mutex
	closed     bool
	stopping   chan struct{}
	readCancel context.CancelFunc
	closeOnce  sync.Once
	closeDone  chan struct{}
	closeError error
}

// Every possibly blocking discovery/open operation retains and disposes its own
// resources after cancellation. The caller returns on its deadline; it cannot
// reuse/free a handle or buffer while an OS request still owns it.
func openOwnedHID(ctx context.Context, candidate ownedHIDCandidate) (*ownedHIDInput, error) {
	type result struct {
		input *ownedHIDInput
		err   error
	}
	done := make(chan result)
	go func() {
		input, err := openOwnedHIDSync(ctx, candidate)
		select {
		case done <- result{input, err}:
		case <-ctx.Done():
			if input != nil {
				_ = input.Close(context.Background())
			}
		}
	}()
	select {
	case r := <-done:
		return r.input, r.err
	case <-ctx.Done():
		return nil, ctx.Err()
	}
}

func openOwnedHIDSync(ctx context.Context, candidate ownedHIDCandidate) (*ownedHIDInput, error) {
	if err := requireOwnedHID(ctx, candidate); err != nil {
		return nil, err
	}
	path, err := windows.UTF16PtrFromString(candidate.Path)
	if err != nil {
		return nil, err
	}
	h, err := windows.CreateFile(path, windows.GENERIC_READ, windows.FILE_SHARE_READ|windows.FILE_SHARE_WRITE, nil, windows.OPEN_EXISTING, windows.FILE_FLAG_OVERLAPPED, 0)
	if err != nil {
		return nil, fmt.Errorf("open own HID read-only: %w", err)
	}
	accepted := false
	defer func() {
		if !accepted {
			_ = windows.CloseHandle(h)
		}
	}()
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	// Verify the opened handle as well as PnP ancestry, closing an open/path-reuse
	// race. These are descriptor reads only; never an output/feature write.
	var serial [128]uint16
	ok, _, e := ownHIDSerial.Call(uintptr(h), uintptr(unsafe.Pointer(&serial[0])), uintptr(unsafe.Sizeof(serial)))
	if ok == 0 {
		return nil, fmt.Errorf("read owned HID serial: %w", e)
	}
	if !strings.EqualFold(windows.UTF16ToString(serial[:]), candidate.Identity.Serial) {
		return nil, errors.New("opened HID serial differs from own attachment")
	}
	attributes := struct {
		Size                     uint32
		Vendor, Product, Version uint16
		Padding                  uint16
	}{Size: 12}
	ok, _, e = ownHIDAttributes.Call(uintptr(h), uintptr(unsafe.Pointer(&attributes)))
	if ok == 0 || attributes.Vendor != 0x054C || attributes.Product != 0x0CE6 {
		return nil, fmt.Errorf("owned HID is not the intended DualSense: %v", e)
	}
	var preparsed uintptr
	ok, _, e = ownHIDPreparsed.Call(uintptr(h), uintptr(unsafe.Pointer(&preparsed)))
	if ok == 0 {
		return nil, e
	}
	defer ownHIDFreePreparsed.Call(preparsed)
	var caps [64]uint16 // HIDP_CAPS is smaller; leading fields hold usage + lengths.
	status, _, _ := ownHIDCaps.Call(preparsed, uintptr(unsafe.Pointer(&caps[0])))
	if uint32(status) != 0x00110000 || caps[0] != 5 || caps[1] != 1 || caps[2] != 64 {
		return nil, errors.New("owned HID has unexpected gamepad usage or report size")
	}
	if err := requireOwnedHID(ctx, candidate); err != nil {
		return nil, err
	}
	reader := &ownedHIDInput{handle: h, candidate: candidate, gate: make(chan struct{}, 1), stopping: make(chan struct{}), closeDone: make(chan struct{})}
	reader.gate <- struct{}{}
	accepted = true
	return reader, nil
}

func (r *ownedHIDInput) Read(ctx context.Context) ([]byte, error) {
	select {
	case <-r.stopping:
		return nil, errors.New("HID observer closed")
	case <-ctx.Done():
		return nil, ctx.Err()
	case <-r.gate:
	}
	r.mu.Lock()
	if r.closed {
		r.mu.Unlock()
		r.gate <- struct{}{}
		return nil, errors.New("HID observer closed")
	}
	opCtx, cancel := context.WithCancel(ctx)
	r.readCancel = cancel
	r.mu.Unlock()
	defer cancel()
	event, err := windows.CreateEvent(nil, 1, 0, nil)
	if err != nil {
		cancel()
		r.mu.Lock()
		r.readCancel = nil
		r.mu.Unlock()
		r.gate <- struct{}{}
		return nil, err
	}
	type result struct {
		report []byte
		err    error
	}
	done := make(chan result, 1)
	submitted := make(chan struct{})
	finished := make(chan struct{})
	cancelled := make(chan struct{})
	buffer := make([]byte, 64)
	ov := &windows.Overlapped{HEvent: event}
	go func() {
		defer close(cancelled)
		select {
		case <-finished:
			return
		case <-opCtx.Done():
		}
		<-submitted
		select {
		case <-finished:
			return
		default:
		}
		_ = windows.CancelIoEx(r.handle, ov)
		runtime.KeepAlive(ov)
	}()
	go func() {
		var actual uint32
		var pin runtime.Pinner
		pin.Pin(&buffer[0])
		pin.Pin(ov)
		pin.Pin(&actual)
		err := windows.ReadFile(r.handle, buffer, &actual, ov)
		close(submitted)
		if err == windows.ERROR_IO_PENDING {
			err = windows.GetOverlappedResult(r.handle, ov, &actual, true)
		}
		close(finished)
		<-cancelled
		runtime.KeepAlive(buffer)
		runtime.KeepAlive(ov)
		pin.Unpin()
		if err == nil && actual != 64 {
			err = errors.New("unexpected owned HID input report length")
		}
		_ = windows.CloseHandle(event)
		r.mu.Lock()
		r.readCancel = nil
		r.mu.Unlock()
		r.gate <- struct{}{}
		if err != nil {
			buffer = nil
		}
		done <- result{buffer, err}
	}()
	select {
	case value := <-done:
		return value.report, value.err
	case <-opCtx.Done():
		return nil, opCtx.Err()
	}
}

// Cancellation begins without waiting: native detach may be what makes an
// outstanding HID request complete. Close confirms the drain afterwards.
func (r *ownedHIDInput) beginClose() {
	if r == nil {
		return
	}
	r.closeOnce.Do(func() {
		r.mu.Lock()
		r.closed = true
		close(r.stopping)
		if r.readCancel != nil {
			r.readCancel()
		}
		r.mu.Unlock()
		go func() { <-r.gate; r.closeError = windows.CloseHandle(r.handle); close(r.closeDone) }()
	})
}

func (r *ownedHIDInput) Close(ctx context.Context) error {
	if r == nil {
		return nil
	}
	r.beginClose()
	select {
	case <-r.closeDone:
		return r.closeError
	case <-ctx.Done():
		return ctx.Err()
	}
}

func waitOwnedHIDRemoved(ctx context.Context, identity ownedHIDIdentity) error {
	for {
		items, err := enumerateOwnedHID(ctx, identity)
		if err != nil {
			return err
		}
		if len(items) == 0 {
			return nil
		}
		select {
		case <-ctx.Done():
			return errors.Join(errors.New("owned DualSense HID removal not confirmed"), ctx.Err())
		case <-time.After(5 * time.Millisecond):
		}
	}
}
