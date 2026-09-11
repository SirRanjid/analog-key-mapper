//go:build windows && amd64

package main

import (
	"context"
	"errors"
	"testing"

	"golang.org/x/sys/windows"
)

type fakeLifecycleWaiter struct {
	status                  uint32
	err                     error
	onWait                  func()
	waits, releases, closes int
}

func (f *fakeLifecycleWaiter) wait(ms uint32) (uint32, error) {
	f.waits++
	if ms != 10 {
		return 0, errors.New("unbounded lifecycle wait")
	}
	if f.onWait != nil {
		f.onWait()
	}
	return f.status, f.err
}
func (f *fakeLifecycleWaiter) release() error { f.releases++; return nil }
func (f *fakeLifecycleWaiter) close() error   { f.closes++; return nil }

func TestLifecycleWaitCancellationAndAbandonment(t *testing.T) {
	for _, mode := range []string{"pre-cancel", "waiting", "acquired-after-cancel", "abandoned", "error", "unexpected", "success"} {
		t.Run(mode, func(t *testing.T) {
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			f := &fakeLifecycleWaiter{status: windows.WAIT_OBJECT_0}
			switch mode {
			case "pre-cancel":
				cancel()
			case "waiting":
				f.status = uint32(windows.WAIT_TIMEOUT)
				f.onWait = cancel
			case "acquired-after-cancel":
				f.onWait = cancel
			case "abandoned":
				f.status = windows.WAIT_ABANDONED
			case "error":
				f.err = errors.New("wait failed")
			case "unexpected":
				f.status = 42
			}
			release, err := waitNativeLifecycle(ctx, f)
			owned := mode == "acquired-after-cancel" || mode == "abandoned" || mode == "success"
			if mode == "success" {
				if err != nil || release == nil || f.releases != 0 || f.closes != 0 {
					t.Fatal("acquisition was not retained:", err)
				}
				if err = release(); err != nil {
					t.Fatal(err)
				}
				if err = release(); err != nil {
					t.Fatal(err)
				}
			} else if err == nil || release != nil {
				t.Fatal("failed acquisition returned ownership")
			}
			wantReleases := 0
			if owned {
				wantReleases = 1
			}
			if f.releases != wantReleases || f.closes != 1 {
				t.Fatalf("wrong cleanup: release=%d close=%d", f.releases, f.closes)
			}
			if mode == "pre-cancel" && f.waits != 0 {
				t.Fatal("canceled acquisition still waited")
			}
		})
	}
}
