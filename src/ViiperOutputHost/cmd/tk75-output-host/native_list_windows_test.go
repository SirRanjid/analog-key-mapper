//go:build windows && amd64

package main

import (
	"context"
	"errors"
	"fmt"
	"reflect"
	"testing"
	"time"

	"golang.org/x/sys/windows"
)

func fakeListItems(count int) []nativeOwnership {
	items := make([]nativeOwnership, count)
	for i := range items {
		items[i] = nativeOwnership{Port: int32(i + 1), Host: "127.0.0.1", Service: 3241,
			BusID: fmt.Sprintf("%d-1", i+1), Serial: fmt.Sprintf("T%014d", i+1)}
	}
	return items
}

func TestNativeListAdaptsPerAttachmentAndAlwaysReadsFresh(t *testing.T) {
	for _, count := range []int{2, 32, 255} {
		t.Run(fmt.Sprint(count), func(t *testing.T) {
			f := &fakeNativeTransport{items: fakeListItems(count)}
			a := &nativeAttachment{transport: f}
			ctx, cancel := context.WithTimeout(context.Background(), time.Second)
			defer cancel()
			deadline, _ := ctx.Deadline()
			items, err := a.list(ctx)
			if err != nil || !reflect.DeepEqual(items, f.items) {
				t.Fatal("complete device list was not read:", err)
			}
			want := []int{4516}
			if count == 32 {
				want = []int{4516, 9028, 18052, 36100}
			}
			if count == 255 {
				want = []int{4516, 9028, 18052, 36100, 72196, 144388, 287644}
			}
			if !reflect.DeepEqual(f.listSizes, want) {
				t.Fatalf("unexpected bounded capacities: %v", f.listSizes)
			}
			for _, observed := range f.listDeadlines {
				if !observed.Equal(deadline) {
					t.Fatal("growth renewed the caller deadline")
				}
			}
			f.items = f.items[:1]
			f.items[0].Serial = "ChangedSerial01"
			before := f.listCalls
			items, err = a.list(ctx)
			if err != nil || len(items) != 1 || items[0].Serial != "ChangedSerial01" || f.listCalls != before+1 {
				t.Fatal("previous identity data was reused:", err)
			}
			if f.listSizes[len(f.listSizes)-1] != want[len(want)-1] {
				t.Fatal("attachment forgot its successful capacity")
			}
			other := &fakeNativeTransport{items: fakeListItems(2)}
			if _, err = (&nativeAttachment{transport: other}).list(ctx); err != nil || other.listSizes[0] != 4516 {
				t.Fatal("capacity leaked to another attachment:", err)
			}
		})
	}
}

func TestNativeListDoesNotRetryOtherErrorsOrExceedPortBound(t *testing.T) {
	for _, failure := range []error{windows.ERROR_ACCESS_DENIED, windows.ERROR_MORE_DATA, context.DeadlineExceeded, errors.New("invalid driver reply")} {
		f := &fakeNativeTransport{listError: failure}
		if items, err := (&nativeAttachment{transport: f}).list(context.Background()); !errors.Is(err, failure) || items != nil || f.listCalls != 1 {
			t.Fatalf("wrong error was retried: %v, calls=%d", err, f.listCalls)
		}
	}
	f := &fakeNativeTransport{items: fakeListItems(256)}
	if items, err := (&nativeAttachment{transport: f}).list(context.Background()); !errors.Is(err, windows.ERROR_INSUFFICIENT_BUFFER) || items != nil || f.listCalls != 7 || f.listSizes[6] != 287644 {
		t.Fatal("maximum list capacity was not enforced:", err, f.listSizes)
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	f = &fakeNativeTransport{items: fakeListItems(32), beforeList: func(int) { cancel() }}
	if items, err := (&nativeAttachment{transport: f}).list(ctx); !errors.Is(err, context.Canceled) || items != nil || f.listCalls != 1 {
		t.Fatal("canceled growth continued:", err, f.listCalls)
	}
}
