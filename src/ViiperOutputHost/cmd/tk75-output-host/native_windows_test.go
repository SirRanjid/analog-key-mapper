//go:build windows && amd64

package main

import (
	"context"
	"encoding/binary"
	"errors"
	"hash/fnv"
	"strconv"
	"strings"
	"testing"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

func TestNativeCandidateLayout(t *testing.T) {
	// This only checks our candidate Go layout. It does not verify a driver.
	if nativeABILayoutVerified {
		t.Fatal("offline layout checks must not claim a live driver verification")
	}
	checks := []struct {
		name string
		got  uintptr
		want uintptr
	}{
		{"location size", unsafe.Sizeof(nativeLocationABI{}), 1096},
		{"attach size", unsafe.Sizeof(nativeAttachABI{}), 1120},
		{"attach location", unsafe.Offsetof(nativeAttachABI{}.Loc), 4},
		{"attach serial", unsafe.Offsetof(nativeAttachABI{}.Serial), 1100},
		{"attach receive mode", unsafe.Offsetof(nativeAttachABI{}.WSKEvents), 1116},
		{"stop size", unsafe.Sizeof(nativeStopABI{}), 1104},
		{"stop count", unsafe.Offsetof(nativeStopABI{}.Count), 1100},
		{"detach size", unsafe.Sizeof(nativeDetachABI{}), 8},
		{"properties size", unsafe.Sizeof(nativePropertiesABI{}), 32},
		{"imported device size", unsafe.Sizeof(nativeImportedDeviceABI{}), 1128},
	}
	for _, c := range checks {
		if c.got != c.want {
			t.Errorf("%s: got %d, want %d", c.name, c.got, c.want)
		}
	}
}

func TestNativeOwnershipRejectsOtherOrReusedPort(t *testing.T) {
	want := nativeOwnership{Port: 7, Host: "127.0.0.1", Service: 3241, BusID: "1-1", Serial: "TK7501234567890"}
	if !want.matchesSnapshot(want) {
		t.Fatal("identical complete snapshot did not match")
	}
	for _, mutate := range []func(*nativeOwnership){
		func(o *nativeOwnership) { o.Port = 0 },
		func(o *nativeOwnership) { o.Port = -1 },
		func(o *nativeOwnership) { o.Host = "localhost" },
		func(o *nativeOwnership) { o.Service++ },
		func(o *nativeOwnership) { o.BusID = "2-1" },
		func(o *nativeOwnership) { o.Serial = "TK7509876543210" },
	} {
		other := want
		mutate(&other)
		if want.matchesSnapshot(other) {
			t.Fatalf("different ownership accepted: %+v", other)
		}
	}
}

// This transport performs no system call, opens no socket and creates no device.
type fakeNativeTransport struct {
	items         []nativeOwnership
	shared        *fakeNativeTransport
	calls         []uint32
	closed        bool
	attachErr     error
	badStop       bool
	listCalls     int
	listError     error
	listSizes     []int
	listDeadlines []time.Time
	beforeList    func(int)
	idleCheck     func() error
}

func (f *fakeNativeTransport) idle(context.Context) error {
	if f.idleCheck != nil {
		return f.idleCheck()
	}
	return nil
}
func (f *fakeNativeTransport) close() error { f.closed = true; return nil }
func (f *fakeNativeTransport) call(ctx context.Context, code uint32, in []byte, outSize int) ([]byte, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	f.calls = append(f.calls, code)
	state := f
	if f.shared != nil {
		state = f.shared
	}
	switch code {
	case nativeIOCTLAttachOnce:
		if len(in) != 1120 || outSize != 8 || binary.LittleEndian.Uint32(in) != 1120 {
			return nil, errors.New("bad attach layout")
		}
		port := int32(7)
		for {
			occupied := false
			for _, item := range state.items {
				occupied = occupied || item.Port == port
			}
			if !occupied {
				break
			}
			port++
		}
		bus, _ := nativeCString(in[8:40])
		serviceText, _ := nativeCString(in[40:72])
		host, _ := nativeCString(in[72:1097])
		serial, _ := nativeCString(in[1100:1116])
		service, _ := strconv.ParseUint(serviceText, 10, 16)
		state.items = append(state.items, nativeOwnership{Port: port, Host: host, Service: uint16(service), BusID: bus, Serial: serial})
		if f.attachErr != nil {
			return nil, f.attachErr
		}
		out := make([]byte, 8)
		binary.LittleEndian.PutUint32(out[4:], uint32(port))
		return out, nil
	case nativeIOCTLStopOwn:
		if len(in) != 1104 || outSize != 1104 || in[8] == 0 || in[40] == 0 || in[72] == 0 {
			return nil, errors.New("incomplete stop")
		}
		if f.badStop {
			return nil, errors.New("stop failed")
		}
		return append([]byte(nil), in...), nil
	case nativeIOCTLList:
		f.listCalls++
		f.listSizes = append(f.listSizes, outSize)
		deadline, _ := ctx.Deadline()
		f.listDeadlines = append(f.listDeadlines, deadline)
		if f.beforeList != nil {
			f.beforeList(f.listCalls)
		}
		if f.listError != nil {
			return nil, f.listError
		}
		if outSize < 4+1128*len(state.items) {
			return nil, windows.ERROR_INSUFFICIENT_BUFFER
		}
		out := make([]byte, 4+1128*len(state.items))
		binary.LittleEndian.PutUint32(out, 1132)
		for i, o := range state.items {
			b := out[4+i*1128:]
			putNativeLocation(b[:1096], o)
			binary.LittleEndian.PutUint32(b, uint32(o.Port))
			copy(b[1108:1124], o.Serial)
		}
		return out, nil
	case nativeIOCTLDetach:
		port := int32(binary.LittleEndian.Uint32(in[4:]))
		if port <= 0 {
			return nil, errors.New("global detach forbidden")
		}
		for i, o := range state.items {
			if o.Port == port {
				state.items = append(state.items[:i], state.items[i+1:]...)
				return nil, nil
			}
		}
		return nil, errors.New("missing fake port")
	default:
		return nil, errors.New("unexpected IOCTL")
	}
}

func fakeOwnedNative() nativeOwnership {
	return nativeOwnership{Port: 7, Host: "127.0.0.1", Service: 3241, BusID: "1-1", Serial: "TK7501234567890"}
}

func fakeNativeHash(host string, service uint16, bus string) (uint32, error) {
	hash := fnv.New32a()
	_, _ = hash.Write([]byte(strings.ToLower(host + "," + strconv.Itoa(int(service)) + "," + bus)))
	return hash.Sum32(), nil
}

func attachFakeNative(own nativeOwnership, tr nativeTransport) (*nativeAttachment, error) {
	return attachNativeTransportWithHasher(context.Background(), own.Host, own.Service, own.BusID, own.Serial, tr, false, fakeNativeHash)
}

func TestNativeAttachAndCleanupUseOnlyOwnIdentity(t *testing.T) {
	own := fakeOwnedNative()
	other := own
	other.Port = 8
	other.BusID = "2-1"
	other.Serial = "OTHER0123456789"
	f := &fakeNativeTransport{items: []nativeOwnership{other}}
	a, err := attachFakeNative(own, f)
	if err != nil {
		t.Fatal(err)
	}
	if err = a.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if !f.closed || len(f.items) != 1 || f.items[0] != other {
		t.Fatal("cleanup changed unrelated identity")
	}
	for _, code := range f.calls {
		if code == 0x22e000 {
			t.Fatal("retrying attach used")
		}
	}
}

func TestNativeCleanupRefusesReusedPort(t *testing.T) {
	own := fakeOwnedNative()
	other := own
	other.Serial = "OTHER0123456789"
	f := &fakeNativeTransport{}
	a, err := attachFakeNative(own, f)
	if err != nil {
		t.Fatal(err)
	}
	f.items = []nativeOwnership{other}
	if err = a.Close(context.Background()); !errors.Is(err, errNativeOwnership) {
		t.Fatalf("expected ownership failure, got %v", err)
	}
	for _, code := range f.calls {
		if code == nativeIOCTLDetach {
			t.Fatal("detached reused port")
		}
	}
	if f.closed {
		t.Fatal("uncertain cleanup acknowledged")
	}
}

func TestNativeAttachFailureRetainsRollbackIdentity(t *testing.T) {
	own := fakeOwnedNative()
	f := &fakeNativeTransport{attachErr: context.DeadlineExceeded}
	a, err := attachFakeNative(own, f)
	if a == nil || !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("partial ownership lost: %v", err)
	}
	if err = a.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if len(f.items) != 0 || !f.closed {
		t.Fatal("late attachment not rolled back")
	}
}

func TestNativeCleanupFailureIsNotSuccess(t *testing.T) {
	own := fakeOwnedNative()
	f := &fakeNativeTransport{badStop: true}
	a, err := attachFakeNative(own, f)
	if err != nil {
		t.Fatal(err)
	}
	if err = a.Close(context.Background()); err == nil || f.closed {
		t.Fatal("failed cleanup acknowledged")
	}
	for _, code := range f.calls {
		if code == nativeIOCTLDetach {
			t.Fatal("detached after stop failure")
		}
	}
}
