//go:build windows && amd64

package main

import (
	"context"
	"crypto/rand"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"fmt"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// This adapter has only been compiled and statically reviewed against the
// pinned source. Earlier fake-transport tests passed; the final native test
// executable was blocked by Windows. No real IOCTL has been issued here.
// Source: vadimgrn/usbip-win2 at 83bd1f781d57ed6efdf15530c55710cf5d4482bc,
// include/usbip/vhci.h. Candidate MSVC x64 layouts below retain base-class
// tail padding explicitly. Go layout tests are not a C++ compiler or binary
// compatibility check, and must not be used as permission to issue IOCTLs.
const (
	nativeUSBIPSourceCommit = "83bd1f781d57ed6efdf15530c55710cf5d4482bc"
	nativeABILayoutVerified = false
	// The published stable IOCTL contract, actual AMD64 PDB field records and
	// matching SYS operand/dispatch evidence support the exclusive Xbox
	// experiment. The PDB GUID mismatch is retained in the review and
	// does not become a full native-ABI or production acceptance claim.
	nativeABIProbeCandidateReviewed = true

	nativeIOCTLAttachOnce = uint32(0x22e018) // function 0x806
	nativeIOCTLDetach     = uint32(0x22e004) // function 0x801; positive port only
	nativeIOCTLList       = uint32(0x22e008) // function 0x802
	nativeIOCTLStopOwn    = uint32(0x22e014) // function 0x805; never empty location
)

var errNativeOwnership = errors.New("USB/IP ownership changed; refusing to detach a different device")

// imported_device_location has a four-byte alignment and three bytes of tail
// padding on MSVC x64. A derived structure starts after that complete base.
type nativeLocationABI struct {
	Port    int32
	BusID   [32]byte
	Service [32]byte
	Host    [1025]byte
	_       [3]byte
}

type nativeAttachABI struct {
	Size uint32
	Loc  nativeLocationABI
	// SERIAL_BUFSZ is 16 in the reviewed release contract. This is a wire
	// field model only; no installed-driver compatibility has been measured.
	Serial    [16]byte
	WSKEvents uint8
	_         [3]byte
}

type nativeStopABI struct {
	Size  uint32
	Loc   nativeLocationABI
	Count int32
}

type nativeDetachABI struct {
	Size uint32
	Port int32
}

type nativePropertiesABI struct {
	DeviceID uint32
	Speed    int32
	Vendor   uint16
	Product  uint16
	Serial   [16]byte
	ISerial  uint8
	WSKEvent uint8
	_        [2]byte
}

type nativeImportedDeviceABI struct {
	Loc        nativeLocationABI
	Properties nativePropertiesABI
}

// nativeOwnership describes an expected identity, not authority to detach.
// Snapshot equality cannot close the race between a device list and detach.
type nativeOwnership struct {
	Port    int32
	Host    string
	Service uint16
	BusID   string
	Serial  string
}

func (o nativeOwnership) matchesSnapshot(other nativeOwnership) bool {
	return o.Port > 0 && o.Host == "127.0.0.1" && o.Service != 0 &&
		validNativeBusID(o.BusID) && validNativeSerial(o.Serial) && o == other
}

func validNativeBusID(busID string) bool {
	parts := strings.Split(busID, "-")
	if len(parts) != 2 || len(busID) >= 32 {
		return false
	}
	for _, part := range parts {
		if part == "" || strings.Trim(part, "0123456789") != "" {
			return false
		}
		n, err := strconv.ParseUint(part, 10, 32)
		if err != nil || n == 0 || strconv.FormatUint(n, 10) != part {
			return false
		}
	}
	return true
}

func validNativeSerial(serial string) bool {
	if len(serial) != 15 {
		return false
	}
	for _, c := range serial {
		if !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) {
			return false
		}
	}
	return true
}

type nativeTransport interface {
	call(context.Context, uint32, []byte, int) ([]byte, error)
	idle(context.Context) error
	close() error
}

// Each attachment retains its own location and random serial even if an attach
// times out. Close can then resolve a late completion without detaching by an
// unverified, possibly reused port number. The lifecycle gate is cancellable.
type nativeAttachment struct {
	ownership       nativeOwnership
	transport       nativeTransport
	gate            chan struct{}
	closed          bool
	probeExclusive  bool
	attachAttempted bool
}

func attachNative(ctx context.Context, host string, service uint16, busID string, probeExclusive bool) (*nativeAttachment, error) {
	// Keep the transport boundary closed even if another future caller omits
	// the earlier resource-free backend check.
	if !probeExclusive || !nativeABIProbeCandidateReviewed {
		return nil, errors.New("USB/IP attachment requires reviewed exclusive ownership")
	}
	ctx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if host != "127.0.0.1" || service == 0 || !validNativeBusID(busID) {
		return nil, fmt.Errorf("invalid private USB/IP location")
	}
	// Hash zero is the kernel's stop-all sentinel, including when all strings
	// are nonempty. Reject it before creating any attachment.
	hash, err := nativeLocationHash(host, service, busID)
	if err != nil || hash == 0 {
		return nil, fmt.Errorf("unsafe USB/IP location hash %d: %v", hash, err)
	}
	var random [7]byte
	if _, err := rand.Read(random[:]); err != nil {
		return nil, err
	}
	serial := "T" + hex.EncodeToString(random[:])
	tr, err := openNativeTransport(ctx)
	if err != nil {
		return nil, err
	}
	return attachNativeTransportWithPolicy(ctx, host, service, busID, serial, tr, probeExclusive)
}

func attachNativeTransport(ctx context.Context, host string, service uint16, busID, serial string, tr nativeTransport) (*nativeAttachment, error) {
	return attachNativeTransportWithPolicy(ctx, host, service, busID, serial, tr, false)
}

func attachNativeTransportWithPolicy(ctx context.Context, host string, service uint16, busID, serial string, tr nativeTransport, probeExclusive bool) (*nativeAttachment, error) {
	a := &nativeAttachment{ownership: nativeOwnership{Host: host, Service: service, BusID: busID, Serial: serial}, transport: tr, gate: make(chan struct{}, 1), probeExclusive: probeExclusive}
	a.gate <- struct{}{}
	if probeExclusive {
		items, err := a.list(ctx)
		if err != nil {
			return a, err
		}
		if len(items) != 0 {
			return a, errors.New("exclusive Xbox mode requires an empty USB/IP controller; no attachment was requested")
		}
	}
	in := make([]byte, 1120)
	binary.LittleEndian.PutUint32(in, 1120)
	putNativeLocation(in[4:1100], a.ownership)
	copy(in[1100:1116], serial)
	// Zero-copy receive mode; no implicit persistent/retry configuration.
	a.attachAttempted = true
	out, err := tr.call(ctx, nativeIOCTLAttachOnce, in, 8)
	if err != nil {
		return a, fmt.Errorf("USB/IP attach: %w", err)
	}
	if len(out) != 8 || int32(binary.LittleEndian.Uint32(out[4:8])) <= 0 {
		return a, errors.New("USB/IP attach returned no positive owned port")
	}
	a.ownership.Port = int32(binary.LittleEndian.Uint32(out[4:8]))
	return a, nil
}

// In the pinned kernel:
//   - STOP_ATTACH_ATTEMPTS matches a case-insensitive 32-bit location hash,
//     not the complete location, and does not prohibit later retry creation;
//   - a network disconnect can schedule reattachment even after ATTACH_ONCE;
//   - detach addresses a reusable hub port, with no compare-and-detach identity;
//   - closing a handle does not remove devices owned by that handle.
//
// The caller accepts these boundaries for its private ephemeral loopback server.
// STOP is scoped to a nonzero hash of all three exact location fields. Snapshot
// matching avoids blind port detach, but the driver offers no atomic identity
// check with detach. This is not an absolute process-crash cleanup guarantee.
func (a *nativeAttachment) Close(ctx context.Context) error {
	if a == nil {
		return nil
	}
	ctx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	if err := a.lock(ctx); err != nil {
		return err
	}
	defer a.unlock()
	if a.closed {
		return nil
	}
	if err := a.transport.idle(ctx); err != nil {
		return fmt.Errorf("pending USB/IP request: %w", err)
	}
	if !a.attachAttempted {
		// A refused baseline check owns only its file handle. In particular,
		// do not STOP or DETACH a device after noticing a pre-existing user.
		if err := a.transport.close(); err != nil {
			return err
		}
		a.closed = true
		return nil
	}
	if err := a.stopOwn(ctx); err != nil {
		return err
	}
	items, err := a.list(ctx)
	if err != nil {
		return err
	}
	for _, o := range items {
		if a.probeExclusive && !a.sameIdentity(o) {
			return errNativeOwnership
		}
		if o.Port == a.ownership.Port && !a.sameIdentity(o) {
			return errNativeOwnership
		}
	}
	for _, o := range items {
		if !a.sameIdentity(o) {
			continue
		}
		if o.Port <= 0 {
			return errNativeOwnership
		}
		if a.probeExclusive {
			// Recheck immediately before the mutation; do not use a stale port
			// list across more than one detach. The kernel's final list-to-port
			// race still requires the documented no-concurrent-users probe.
			current, err := a.list(ctx)
			if err != nil {
				return err
			}
			matched := false
			for _, now := range current {
				if !a.sameIdentity(now) {
					return errNativeOwnership
				}
				matched = matched || now == o
			}
			if !matched {
				return errNativeOwnership
			}
		}
		in := make([]byte, 8)
		binary.LittleEndian.PutUint32(in, 8)
		binary.LittleEndian.PutUint32(in[4:], uint32(o.Port))
		if _, err := a.transport.call(ctx, nativeIOCTLDetach, in, 0); err != nil {
			return fmt.Errorf("detach owned USB/IP port: %w", err)
		}
	}
	// Stop again after detach, then require the exact identity to be absent.
	// Any later kernel retry remains an explicitly documented boundary.
	if err := a.stopOwn(ctx); err != nil {
		return err
	}
	items, err = a.list(ctx)
	if err != nil {
		return err
	}
	for _, o := range items {
		if a.sameIdentity(o) {
			return errors.New("owned USB/IP device remains after detach")
		}
	}
	if err := a.transport.close(); err != nil {
		return err
	}
	a.closed = true
	return nil
}

func (a *nativeAttachment) Alive(ctx context.Context) error {
	if a == nil {
		return errors.New("no USB/IP attachment")
	}
	ctx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	if err := a.lock(ctx); err != nil {
		return err
	}
	defer a.unlock()
	if a.closed {
		return errors.New("USB/IP attachment closed")
	}
	items, err := a.list(ctx)
	if err != nil {
		return err
	}
	for _, o := range items {
		if a.probeExclusive && !a.sameIdentity(o) {
			return errNativeOwnership
		}
	}
	for _, o := range items {
		if a.sameIdentity(o) && o.Port > 0 {
			return nil
		}
	}
	return errors.New("owned USB/IP attachment is absent")
}

func (a *nativeAttachment) lock(ctx context.Context) error {
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-a.gate:
		return nil
	}
}
func (a *nativeAttachment) unlock() { a.gate <- struct{}{} }
func (a *nativeAttachment) sameIdentity(o nativeOwnership) bool {
	return o.Host == a.ownership.Host && o.Service == a.ownership.Service && o.BusID == a.ownership.BusID && o.Serial == a.ownership.Serial && validNativeSerial(o.Serial)
}
func putNativeLocation(dst []byte, o nativeOwnership) {
	copy(dst[4:36], o.BusID)
	copy(dst[36:68], strconv.FormatUint(uint64(o.Service), 10))
	copy(dst[68:1093], o.Host)
}

func (a *nativeAttachment) stopOwn(ctx context.Context) error {
	if a.ownership.Host != "127.0.0.1" || a.ownership.Service == 0 || !validNativeBusID(a.ownership.BusID) {
		return errors.New("refusing incomplete STOP location")
	}
	if a.probeExclusive {
		items, err := a.list(ctx)
		if err != nil {
			return err
		}
		for _, o := range items {
			if !a.sameIdentity(o) {
				return errNativeOwnership
			}
		}
	}
	in := make([]byte, 1104)
	binary.LittleEndian.PutUint32(in, 1104)
	putNativeLocation(in[4:1100], a.ownership)
	out, err := a.transport.call(ctx, nativeIOCTLStopOwn, in, 1104)
	if err != nil {
		return fmt.Errorf("stop own USB/IP attach attempts: %w", err)
	}
	if len(out) != 1104 || int32(binary.LittleEndian.Uint32(out[1100:])) < 0 {
		return errors.New("invalid STOP response")
	}
	return nil
}

func (a *nativeAttachment) list(ctx context.Context) ([]nativeOwnership, error) {
	// The current driver has at most 255 ports. A fixed bounded buffer avoids
	// retries or untrusted length-driven allocations during ownership checks.
	in := make([]byte, 4)
	binary.LittleEndian.PutUint32(in, 1132)
	out, err := a.transport.call(ctx, nativeIOCTLList, in, 4+255*1128)
	if err != nil {
		return nil, fmt.Errorf("list USB/IP ownership: %w", err)
	}
	if len(out) < 4 || (len(out)-4)%1128 != 0 {
		return nil, errors.New("invalid imported-device response length")
	}
	var items []nativeOwnership
	for off := 4; off < len(out); off += 1128 {
		b := out[off : off+1128]
		bus, err := nativeCString(b[4:36])
		if err != nil {
			return nil, err
		}
		service, err := nativeCString(b[36:68])
		if err != nil {
			return nil, err
		}
		host, err := nativeCString(b[68:1093])
		if err != nil {
			return nil, err
		}
		serial, err := nativeCString(b[1108:1124])
		if err != nil {
			return nil, err
		}
		n, err := strconv.ParseUint(service, 10, 16)
		if err != nil {
			return nil, err
		}
		items = append(items, nativeOwnership{Port: int32(binary.LittleEndian.Uint32(b)), Host: host, Service: uint16(n), BusID: bus, Serial: serial})
	}
	return items, nil
}

func nativeCString(b []byte) (string, error) {
	for i, c := range b {
		if c == 0 {
			return string(b[:i]), nil
		}
	}
	return "", errors.New("unterminated USB/IP identity")
}

var nativeSetupAPI = windows.NewLazySystemDLL("setupapi.dll")
var nativeGetClass = nativeSetupAPI.NewProc("SetupDiGetClassDevsW")
var nativeEnum = nativeSetupAPI.NewProc("SetupDiEnumDeviceInterfaces")
var nativeDetail = nativeSetupAPI.NewProc("SetupDiGetDeviceInterfaceDetailW")
var nativeDestroy = nativeSetupAPI.NewProc("SetupDiDestroyDeviceInfoList")
var nativeHashProc = windows.NewLazySystemDLL("ntdll.dll").NewProc("RtlHashUnicodeString")
var nativeDriverGUID = windows.GUID{Data1: 0xB4030C06, Data2: 0xDC5F, Data3: 0x4FCC, Data4: [8]byte{0x87, 0xEB, 0xE5, 0x51, 0x5A, 0x09, 0x35, 0xC0}}

func nativeLocationHash(host string, service uint16, busID string) (uint32, error) {
	w, err := windows.UTF16FromString(host + "," + strconv.FormatUint(uint64(service), 10) + "," + busID)
	if err != nil {
		return 0, err
	}
	type unicodeString struct {
		Length, Maximum uint16
		Buffer          *uint16
	}
	u := unicodeString{Length: uint16((len(w) - 1) * 2), Maximum: uint16(len(w) * 2), Buffer: &w[0]}
	var hash uint32
	r, _, _ := nativeHashProc.Call(uintptr(unsafe.Pointer(&u)), 1, 0, uintptr(unsafe.Pointer(&hash)))
	runtime.KeepAlive(w)
	if int32(r) < 0 {
		return 0, fmt.Errorf("RtlHashUnicodeString status %#x", r)
	}
	return hash, nil
}

type nativeWindowsTransport struct {
	controllerID string
	handle       windows.Handle
	gate         chan struct{}
	mu           sync.Mutex
	closed       bool
}

func openNativeTransport(ctx context.Context) (nativeTransport, error) {
	type result struct {
		tr  nativeTransport
		err error
	}
	done := make(chan result)
	go func() {
		tr, err := discoverNativeTransport()
		select {
		case done <- result{tr, err}:
		case <-ctx.Done():
			if tr != nil {
				_ = tr.close()
			}
		}
	}()
	select {
	case <-ctx.Done():
		return nil, ctx.Err()
	case r := <-done:
		return r.tr, r.err
	}
}

func discoverNativeTransport() (*nativeWindowsTransport, error) {
	r, _, e := nativeGetClass.Call(uintptr(unsafe.Pointer(&nativeDriverGUID)), 0, 0, 0x12)
	if windows.Handle(r) == windows.InvalidHandle {
		return nil, fmt.Errorf("USB/IP discovery: %w", e)
	}
	defer nativeDestroy.Call(r)
	type iface struct {
		Size     uint32
		GUID     windows.GUID
		Flags    uint32
		Reserved uintptr
	}
	x := iface{Size: uint32(unsafe.Sizeof(iface{}))}
	ok, _, e := nativeEnum.Call(r, 0, uintptr(unsafe.Pointer(&nativeDriverGUID)), 0, uintptr(unsafe.Pointer(&x)))
	if ok == 0 {
		return nil, fmt.Errorf("USB/IP driver interface: %w", e)
	}
	// A probe must not silently choose one of multiple installed USB/IP
	// controller instances while checking only that instance's device list.
	other := iface{Size: uint32(unsafe.Sizeof(iface{}))}
	more, _, moreErr := nativeEnum.Call(r, 0, uintptr(unsafe.Pointer(&nativeDriverGUID)), 1, uintptr(unsafe.Pointer(&other)))
	if more != 0 {
		return nil, errors.New("multiple USB/IP controller interfaces; neutral probe requires exactly one")
	}
	if moreErr != windows.ERROR_NO_MORE_ITEMS {
		return nil, fmt.Errorf("USB/IP additional-interface check: %w", moreErr)
	}
	var needed uint32
	ok, _, e = nativeDetail.Call(r, uintptr(unsafe.Pointer(&x)), 0, 0, uintptr(unsafe.Pointer(&needed)), 0)
	if ok == 0 && e != windows.ERROR_INSUFFICIENT_BUFFER {
		return nil, e
	}
	if needed < 6 || needed > 65536 {
		return nil, errors.New("invalid USB/IP device path length")
	}
	b := make([]byte, needed)
	binary.LittleEndian.PutUint32(b, 8)
	devInfo := hidDevInfo{Size: uint32(unsafe.Sizeof(hidDevInfo{}))}
	ok, _, e = nativeDetail.Call(r, uintptr(unsafe.Pointer(&x)), uintptr(unsafe.Pointer(&b[0])), uintptr(needed), 0, uintptr(unsafe.Pointer(&devInfo)))
	if ok == 0 {
		return nil, e
	}
	controllerID, err := hidDevNodeID(devInfo.DevInst)
	if err != nil {
		return nil, fmt.Errorf("USB/IP controller identity: %w", err)
	}
	p := (*uint16)(unsafe.Pointer(&b[4]))
	h, err := windows.CreateFile(p, windows.GENERIC_READ|windows.GENERIC_WRITE, windows.FILE_SHARE_READ|windows.FILE_SHARE_WRITE, nil, windows.OPEN_EXISTING, windows.FILE_FLAG_OVERLAPPED, 0)
	runtime.KeepAlive(b)
	if err != nil {
		return nil, fmt.Errorf("open USB/IP driver: %w", err)
	}
	t := &nativeWindowsTransport{handle: h, gate: make(chan struct{}, 1), controllerID: controllerID}
	t.gate <- struct{}{}
	return t, nil
}

// Exact controller devnode associated with the interface opened above. It is
// not inferred from a product name, USB hub number, or a system-wide VID/PID.
func (t *nativeWindowsTransport) ControllerIdentity() string { return t.controllerID }

func (a *nativeAttachment) HIDIdentity(ctx context.Context) (ownedHIDIdentity, error) {
	if a == nil {
		return ownedHIDIdentity{}, errors.New("missing own attachment")
	}
	if err := a.lock(ctx); err != nil {
		return ownedHIDIdentity{}, err
	}
	defer a.unlock()
	if a.closed {
		return ownedHIDIdentity{}, errors.New("own attachment closed")
	}
	source, ok := a.transport.(interface{ ControllerIdentity() string })
	if !ok || source.ControllerIdentity() == "" {
		return ownedHIDIdentity{}, errors.New("opened USB/IP controller devnode identity unavailable")
	}
	items, err := a.list(ctx)
	if err != nil {
		return ownedHIDIdentity{}, err
	}
	count := 0
	for _, item := range items {
		if item.Port == a.ownership.Port && !a.sameIdentity(item) {
			return ownedHIDIdentity{}, errNativeOwnership
		}
		if a.sameIdentity(item) {
			if !a.ownership.matchesSnapshot(item) {
				return ownedHIDIdentity{}, errNativeOwnership
			}
			count++
		}
	}
	if count != 1 {
		return ownedHIDIdentity{}, errors.New("own attachment identity is not uniquely present")
	}
	return ownedHIDIdentity{ControllerID: source.ControllerIdentity(), Serial: a.ownership.Serial}, nil
}

func (t *nativeWindowsTransport) idle(ctx context.Context) error {
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-t.gate:
		t.gate <- struct{}{}
		return nil
	}
}
func (t *nativeWindowsTransport) close() error {
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.closed {
		return nil
	}
	select {
	case <-t.gate:
		defer func() { t.gate <- struct{}{} }()
	default:
		return errors.New("native request still pending")
	}
	err := windows.CloseHandle(t.handle)
	if err == nil {
		t.closed = true
	}
	return err
}

func (t *nativeWindowsTransport) call(ctx context.Context, code uint32, input []byte, outSize int) ([]byte, error) {
	select {
	case <-ctx.Done():
		return nil, ctx.Err()
	case <-t.gate:
	}
	t.mu.Lock()
	closed := t.closed
	t.mu.Unlock()
	if closed {
		t.gate <- struct{}{}
		return nil, errors.New("native transport closed")
	}
	event, err := windows.CreateEvent(nil, 1, 0, nil)
	if err != nil {
		t.gate <- struct{}{}
		return nil, err
	}
	in := append([]byte(nil), input...)
	out := make([]byte, outSize)
	ov := &windows.Overlapped{HEvent: event}
	type result struct {
		data []byte
		err  error
	}
	done := make(chan result, 1)
	submitted := make(chan struct{})
	finished := make(chan struct{})
	cancelDone := make(chan struct{})
	go func() {
		defer close(cancelDone)
		select {
		case <-finished:
			return
		case <-ctx.Done():
		}
		<-submitted
		select {
		case <-finished:
			return
		default:
		}
		_ = windows.CancelIoEx(t.handle, ov)
		runtime.KeepAlive(ov)
	}()
	go func() {
		var inPtr, outPtr *byte
		var pinned runtime.Pinner
		pinned.Pin(ov)
		if len(in) > 0 {
			inPtr = &in[0]
			pinned.Pin(inPtr)
		}
		if len(out) > 0 {
			outPtr = &out[0]
			pinned.Pin(outPtr)
		}
		var actual uint32
		pinned.Pin(&actual)
		err := windows.DeviceIoControl(t.handle, code, inPtr, uint32(len(in)), outPtr, uint32(len(out)), &actual, ov)
		close(submitted)
		if err == windows.ERROR_IO_PENDING {
			err = windows.GetOverlappedResult(t.handle, ov, &actual, true)
		}
		close(finished)
		<-cancelDone
		runtime.KeepAlive(in)
		runtime.KeepAlive(out)
		runtime.KeepAlive(ov)
		pinned.Unpin()
		if err == nil && actual > uint32(len(out)) {
			err = errors.New("native response exceeds buffer")
		}
		if err == nil {
			out = out[:actual]
		} else {
			out = nil
		}
		// Publish the result only after releasing every per-request resource;
		// an immediate Close must not see a request that has already completed.
		_ = windows.CloseHandle(event)
		t.gate <- struct{}{}
		done <- result{out, err}
	}()
	select {
	case r := <-done:
		return r.data, r.err
	case <-ctx.Done():
		// Cancellation may precede submission. Retain the request and buffers
		// until the kernel completes it; no handle is closed while pending.
		return nil, ctx.Err()
	}
}
