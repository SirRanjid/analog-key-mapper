//go:build windows && amd64

package main

import (
	"context"
	"errors"
	"fmt"
	"runtime"
	"sync"

	"golang.org/x/sys/windows"
)

// Cooperating mapper helpers serialize topology changes, not FRAME/Alive.
// This does not lock out unrelated USB/IP software or create an atomic kernel
// compare-and-detach operation. Never change machine-wide driver state here.
const nativeLifecycleMutexName = `Global\AnalogKeyMapper.UsbIpLifecycle.v1`

type nativeLifecycleWaiter interface {
	wait(uint32) (uint32, error)
	release() error
	close() error
}

type windowsLifecycleMutex struct{ handle windows.Handle }

func (m windowsLifecycleMutex) wait(ms uint32) (uint32, error) {
	return windows.WaitForSingleObject(m.handle, ms)
}
func (m windowsLifecycleMutex) release() error { return windows.ReleaseMutex(m.handle) }
func (m windowsLifecycleMutex) close() error   { return windows.CloseHandle(m.handle) }

// The caller MUST release on the same goroutine; the OS thread remains pinned
// until release. Hold through the final own native request completion, including
// canceled OVERLAPPED requests; transport.idle(context.Background()) supplies
// that boundary while the outer session deadline remains independently bounded.
func acquireNativeLifecycle(ctx context.Context) (func() error, error) {
	return acquireNativeLifecycleNamed(ctx, nativeLifecycleMutexName)
}

func finishNativeLifecycle(release func() error, attachment *nativeAttachment) error {
	if attachment != nil && attachment.transport != nil {
		if err := attachment.transport.idle(context.Background()); err != nil {
			// Do not allow another attach to pass an unsettled native request.
			// The outer host deadline can still fail and terminate this process.
			return fmt.Errorf("controller lifecycle request did not settle: %w", err)
		}
	}
	return release()
}

func acquireNativeLifecycleNamed(ctx context.Context, name string) (func() error, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	wide, err := windows.UTF16PtrFromString(name)
	if err != nil {
		return nil, err
	}
	// SYNCHRONIZE | MUTEX_MODIFY_STATE, rather than unnecessary full access.
	handle, err := windows.CreateMutexEx(nil, wide, 0, windows.SYNCHRONIZE|1)
	if err != nil {
		return nil, fmt.Errorf("open controller lifecycle mutex: %w", err)
	}
	runtime.LockOSThread()
	ownerThread := windows.GetCurrentThreadId()
	release, err := waitNativeLifecycle(ctx, windowsLifecycleMutex{handle})
	if err != nil {
		runtime.UnlockOSThread()
		return nil, err
	}
	var once sync.Once
	var result error
	return func() error {
		if windows.GetCurrentThreadId() != ownerThread {
			return errors.New("controller lifecycle mutex must be released by its owning thread")
		}
		once.Do(func() {
			result = release()
			runtime.UnlockOSThread()
		})
		return result
	}, nil
}

// Injectable wait boundary; tests exercise cancellation and abandonment without
// opening a driver, creating a device, or changing the real lifecycle mutex.
func waitNativeLifecycle(ctx context.Context, waiter nativeLifecycleWaiter) (func() error, error) {
	for {
		if err := ctx.Err(); err != nil {
			return nil, errors.Join(err, waiter.close())
		}
		status, err := waiter.wait(10)
		if err != nil {
			return nil, errors.Join(fmt.Errorf("wait for controller lifecycle: %w", err), waiter.close())
		}
		switch status {
		case uint32(windows.WAIT_TIMEOUT):
			continue
		case windows.WAIT_ABANDONED:
			// Windows grants ownership for WAIT_ABANDONED too. Release it, but
			// never treat an interrupted previous topology change as success.
			return nil, errors.Join(errors.New("previous controller lifecycle ended unexpectedly; no new operation was started"), waiter.release(), waiter.close())
		case windows.WAIT_OBJECT_0:
			if err := ctx.Err(); err != nil {
				return nil, errors.Join(err, waiter.release(), waiter.close())
			}
			var once sync.Once
			var result error
			return func() error {
				once.Do(func() { result = errors.Join(waiter.release(), waiter.close()) })
				return result
			}, nil
		default:
			return nil, errors.Join(fmt.Errorf("unexpected controller lifecycle wait result %#x", status), waiter.close())
		}
	}
}
