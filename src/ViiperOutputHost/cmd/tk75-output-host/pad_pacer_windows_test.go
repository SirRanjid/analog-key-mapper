//go:build windows && amd64

package main

import (
	"bytes"
	"testing"
	"time"

	"github.com/Alia5/VIIPER/device/xbox360"
	"github.com/Alia5/VIIPER/usbip"
)

func TestXboxPacerUnchangedFramesWaitChangesAndNeutralWake(t *testing.T) {
	p, err := newTrackedPad()
	if err != nil {
		t.Fatal(err)
	}
	p.pacer.interval = time.Hour
	neutral := make([]byte, 20)
	neutral[1] = 20
	if first := p.HandleTransfer(1, usbip.DirIn, nil); !bytes.Equal(first, neutral) {
		t.Fatal("startup report is not neutral")
	}
	request := func() (<-chan struct{}, *[]byte) {
		done := make(chan struct{})
		var report []byte
		go func() { report = p.HandleTransfer(1, usbip.DirIn, nil); close(done) }()
		t.Cleanup(func() { p.pacer.changed(); awaitPacerTest(t, done) })
		return done, &report
	}
	pressed := xbox360.InputState{Buttons: 0x1000, LT: 71, LX: -12345}
	activeDone, activeReport := request()
	for i := 0; i < 1000; i++ {
		p.UpdateInputState(xbox360.InputState{})
	}
	assertPacerWaiting(t, activeDone)
	p.UpdateInputState(pressed)
	awaitPacerTest(t, activeDone)
	if !bytes.Equal(*activeReport, pressed.BuildReport()) {
		t.Fatalf("woken request did not read the latest state: % X", *activeReport)
	}
	neutralDone, neutralReport := request()
	for i := 0; i < 1000; i++ {
		p.UpdateInputState(pressed)
	}
	assertPacerWaiting(t, neutralDone)
	p.UpdateInputState(xbox360.InputState{})
	awaitPacerTest(t, neutralDone)
	if !bytes.Equal(*neutralReport, neutral) {
		t.Fatalf("neutralization did not wake a neutral report: % X", *neutralReport)
	}
}

func TestXboxPacerLeavesOtherEndpointsAndOutputUnpaced(t *testing.T) {
	p, err := newTrackedPad()
	if err != nil {
		t.Fatal(err)
	}
	p.pacer.interval = time.Hour
	p.HandleTransfer(1, usbip.DirIn, nil)
	done := make(chan struct{})
	go func() {
		p.HandleTransfer(2, usbip.DirIn, nil)
		p.HandleTransfer(1, usbip.DirOut, nil)
		close(done)
	}()
	awaitPacerTest(t, done)
}
