//go:build windows && amd64

package main

import (
	"context"
	"encoding/binary"
	"errors"
	"testing"
	"unsafe"
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
	items     []nativeOwnership
	calls     []uint32
	closed    bool
	attachErr error
	badStop   bool
}

func (f *fakeNativeTransport) idle(context.Context) error { return nil }
func (f *fakeNativeTransport) close() error               { f.closed = true; return nil }
func (f *fakeNativeTransport) call(ctx context.Context, code uint32, in []byte, outSize int) ([]byte, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	f.calls = append(f.calls, code)
	switch code {
	case nativeIOCTLAttachOnce:
		if len(in) != 1120 || outSize != 8 || binary.LittleEndian.Uint32(in) != 1120 {
			return nil, errors.New("bad attach layout")
		}
		if f.attachErr != nil {
			return nil, f.attachErr
		}
		out := make([]byte, 8)
		binary.LittleEndian.PutUint32(out[4:], 7)
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
		out := make([]byte, 4+1128*len(f.items))
		binary.LittleEndian.PutUint32(out, 1132)
		for i, o := range f.items {
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
		for i, o := range f.items {
			if o.Port == port {
				f.items = append(f.items[:i], f.items[i+1:]...)
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

func TestNativeAttachAndCleanupUseOnlyOwnIdentity(t *testing.T) {
	own := fakeOwnedNative()
	other := own
	other.Port = 8
	other.Serial = "OTHER0123456789"
	f := &fakeNativeTransport{items: []nativeOwnership{own, other}}
	a, err := attachNativeTransport(context.Background(), own.Host, own.Service, own.BusID, own.Serial, f)
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
	f := &fakeNativeTransport{items: []nativeOwnership{other}}
	a, err := attachNativeTransport(context.Background(), own.Host, own.Service, own.BusID, own.Serial, f)
	if err != nil {
		t.Fatal(err)
	}
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
	f := &fakeNativeTransport{items: []nativeOwnership{own}, attachErr: context.DeadlineExceeded}
	a, err := attachNativeTransport(context.Background(), own.Host, own.Service, own.BusID, own.Serial, f)
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
	f := &fakeNativeTransport{items: []nativeOwnership{own}, badStop: true}
	a, err := attachNativeTransport(context.Background(), own.Host, own.Service, own.BusID, own.Serial, f)
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
