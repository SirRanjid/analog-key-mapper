package main

import (
	"bufio"
	"context"
	"encoding/base64"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"strconv"
	"strings"
	"sync"
	"time"
)

const wireVersion = "TK75OUT/1"
const maxCommand = 512

type packet struct {
	Buttons        uint16
	LT, RT         uint8
	LX, LY, RX, RY int16
}

// Input freedom never disables native ownership checks. In particular, an
// accepted neutral probe does not allow a non-exclusive/multi-controller run.
func xboxAttachmentAllowed(neutralOnly, exclusiveOwnership, reviewedABI, runtimeAccepted bool) bool {
	return exclusiveOwnership && reviewedABI && (neutralOnly || runtimeAccepted)
}

// Independent byte conversion only: no VIIPER import, device, DLL or transport.
// TK75OUT/1 carries common gamepad controls, not PS/touch/mic/Edge controls.
// The 33-byte layout is pinned to VIIPER v0.7.0 device/dualsense/state.go.
func encodeDualSense(p packet) ([33]byte, error) {
	var state [33]byte
	if p.Buttons & ^uint16(0xF3FF) != 0 {
		return state, errors.New("unsupported common controller button bits")
	}
	state[0] = byte(dualSenseAxis(p.LX, false))
	state[1] = byte(dualSenseAxis(p.LY, true))
	state[2] = byte(dualSenseAxis(p.RX, false))
	state[3] = byte(dualSenseAxis(p.RY, true))
	var buttons uint32
	for _, pair := range [][2]uint32{
		{0x1000, 0x0020}, {0x2000, 0x0040}, {0x4000, 0x0010}, {0x8000, 0x0080},
		{0x0100, 0x0100}, {0x0200, 0x0200}, {0x0020, 0x1000}, {0x0010, 0x2000},
		{0x0040, 0x4000}, {0x0080, 0x8000},
	} {
		if uint32(p.Buttons)&pair[0] != 0 {
			buttons |= pair[1]
		}
	}
	// A DualSense also has digital L2/R2 bits. They follow its post-processing
	// analog trigger output; they never independently generate a trigger press.
	if p.LT != 0 {
		buttons |= 0x0400
	}
	if p.RT != 0 {
		buttons |= 0x0800
	}
	binary.LittleEndian.PutUint32(state[4:8], buttons)
	dpad := byte(p.Buttons & 0x0F)
	// A HID hat cannot represent simultaneous opposing directions. Neutralize
	// each contradictory pair; preserve a remaining orthogonal direction.
	if dpad&0x03 == 0x03 {
		dpad &^= 0x03
	}
	if dpad&0x0C == 0x0C {
		dpad &^= 0x0C
	}
	state[8], state[9], state[10] = dpad, p.LT, p.RT
	// No touch or angular motion. Preserve VIIPER's stationary gravity value
	// on every frame, including NEUTRAL, instead of switching to zero-g.
	binary.LittleEndian.PutUint16(state[31:33], 0xE000) // int16(-8192)
	return state, nil
}

func dualSenseAxis(value int16, invert bool) int8 {
	v := int32(value)
	denominator := int32(32767)
	negative := v < 0
	if negative {
		denominator = 32768
		v = -v
	}
	if invert {
		negative = !negative
	}
	span := int32(127)
	if negative {
		span = 128
	}
	result := (v*span + denominator/2) / denominator
	if negative {
		result = -result
	}
	return int8(result)
}

// This validates gameplay-neutral fields, not a report full of zero bytes.
// It is only an encoder/report utility, never proof of Windows HID readback.
func neutralDualSenseReport(report []byte) bool {
	if len(report) != 64 || report[0] != 1 || report[1] != 128 || report[2] != 128 ||
		report[3] != 128 || report[4] != 128 || report[5] != 0 || report[6] != 0 ||
		report[8] != 8 || report[9] != 0 || report[10] != 0 {
		return false
	}
	for i := 16; i < 26; i++ {
		if report[i] != 0 {
			return false
		}
	}
	return binary.LittleEndian.Uint16(report[26:28]) == 0xE000 && report[33]&0x80 != 0 && report[37]&0x80 != 0
}

const dualSenseAcceptanceRequired = "DualSense output is not released: the implemented owned-HID backend still requires ABI verification and a separately authorized real startup-neutrality and removal acceptance. No virtual device was started."

// VIIPER's report timestamp is an incrementing uint32 counter. Ignore buffered
// reports from before a requested neutral update, including across wraparound.
func dualSenseReportAfter(report []byte, before uint32) bool {
	return len(report) == 64 && report[0] == 1 && int32(binary.LittleEndian.Uint32(report[28:32])-before) > 0
}

// Pure ownership predicate. The chain is collected from Windows devnodes, never
// parsed out of a symbolic interface path. USB serial equality alone is not
// enough: the exact controller opened for this attachment must be its ancestor.
type ownedHIDIdentity struct{ ControllerID, Serial string }

func (o ownedHIDIdentity) matchesAncestors(chain []string) bool {
	if o.ControllerID == "" || len(o.Serial) != 15 || len(chain) < 3 {
		return false
	}
	for _, c := range o.Serial {
		if !((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) {
			return false
		}
	}
	wantedUSB := "USB\\VID_054C&PID_0CE6\\" + o.Serial
	usbFound := false
	for _, id := range chain {
		if strings.EqualFold(id, wantedUSB) {
			usbFound = true
			continue
		}
		if strings.EqualFold(id, o.ControllerID) {
			return usbFound
		}
	}
	return false
}

// Fail closed before native work. A different command-line switch, FRAME, or
// environment variable cannot turn the missing DualSense acceptance into ACK.
type unavailableOutput struct{ reason string }

func (b *unavailableOutput) Connect(ctx context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	return errors.New(b.reason)
}
func (b *unavailableOutput) Submit(ctx context.Context, p packet) error { return errors.New(b.reason) }
func (b *unavailableOutput) Neutral(ctx context.Context) error          { return nil } // never attached
func (b *unavailableOutput) Close(ctx context.Context) error            { return nil } // owns no resources

// Implementations must honor cancellation and permit Close to cancel a pending
// operation. A timeout is failure, never an acknowledgement of removal.
type outputBackend interface {
	Connect(context.Context) error
	Submit(context.Context, packet) error
	Neutral(context.Context) error
	Close(context.Context) error
}

type hostLimits struct {
	heartbeat, command, connect, cleanup time.Duration
	neutralCleanup, removalCleanup       time.Duration
}

var normalLimits = hostLimits{heartbeat: 250 * time.Millisecond, command: 200 * time.Millisecond, connect: 12 * time.Second, cleanup: 180 * time.Millisecond}

// The acceptance probe and accepted exclusive runtime wait for PnP removal. This
// changes neither the 250 ms feeder watchdog nor the immediate neutral request.
var probeLimits = func() hostLimits {
	l := normalLimits
	l.neutralCleanup = 60 * time.Millisecond
	l.removalCleanup = 2500 * time.Millisecond
	return l
}()

func bounded(parent context.Context, timeout time.Duration, operation func(context.Context) error) error {
	ctx, cancel := context.WithTimeout(parent, timeout)
	defer cancel()
	done := make(chan error, 1)
	go func() { done <- operation(ctx) }()
	select {
	case err := <-done:
		return err
	case <-ctx.Done():
		return ctx.Err()
	}
}

type session struct {
	mu              sync.Mutex
	backend         outputBackend
	active, stopped bool
	deadline        time.Time
	limits          hostLimits
	ctx             context.Context
	cancel          context.CancelFunc
	fault           chan error
	wake            chan struct{}
	cleanupOnce     sync.Once
	cleanupDone     chan struct{}
	cleanupErr      error
}

func newSession(limits hostLimits) *session {
	ctx, cancel := context.WithCancel(context.Background())
	s := &session{limits: limits, ctx: ctx, cancel: cancel, fault: make(chan error, 1), wake: make(chan struct{}, 1), cleanupDone: make(chan struct{})}
	go s.watch()
	return s
}

func (s *session) fail(err error) {
	s.mu.Lock()
	s.stopped = true
	s.active = false
	s.mu.Unlock()
	s.cancel()
	select {
	case s.fault <- err:
	default:
	}
	go s.close()
}

func (s *session) watch() {
	for {
		s.mu.Lock()
		active, deadline := s.active, s.deadline
		s.mu.Unlock()
		if !active {
			select {
			case <-s.ctx.Done():
				return
			case <-s.wake:
				continue
			}
		}
		wait := time.Until(deadline)
		if wait <= 0 {
			s.fail(errors.New("input heartbeat expired; controller removal requested"))
			return
		}
		timer := time.NewTimer(wait)
		select {
		case <-s.ctx.Done():
			timer.Stop()
			return
		case <-s.wake:
			timer.Stop()
		case <-timer.C:
			s.mu.Lock()
			expired := s.active && !time.Now().Before(s.deadline)
			s.mu.Unlock()
			if expired {
				s.fail(errors.New("input heartbeat expired; controller removal requested"))
				return
			}
		}
	}
}

func (s *session) connect(factory func() outputBackend) error {
	s.mu.Lock()
	if s.stopped || s.backend != nil {
		s.mu.Unlock()
		return errors.New("CONNECT is only permitted once")
	}
	b := factory()
	if b == nil {
		s.mu.Unlock()
		return errors.New("backend unavailable")
	}
	s.backend = b
	s.mu.Unlock()
	if err := bounded(s.ctx, s.limits.connect, b.Connect); err != nil {
		return err
	}
	if err := bounded(s.ctx, s.limits.command, b.Neutral); err != nil {
		return err
	}
	s.mu.Lock()
	if s.stopped {
		s.mu.Unlock()
		return errors.New("session stopped during CONNECT")
	}
	s.active = true
	s.deadline = time.Now().Add(s.limits.heartbeat)
	s.mu.Unlock()
	select {
	case s.wake <- struct{}{}:
	default:
	}
	return nil
}

func (s *session) submit(p packet, neutral bool) error {
	s.mu.Lock()
	if !s.active || s.stopped || !time.Now().Before(s.deadline) {
		s.mu.Unlock()
		return errors.New("controller disconnected or heartbeat expired")
	}
	b := s.backend
	s.mu.Unlock()
	operation := func(ctx context.Context) error { return b.Submit(ctx, p) }
	if neutral {
		operation = b.Neutral
	}
	if err := bounded(s.ctx, s.limits.command, operation); err != nil {
		return err
	}
	s.mu.Lock()
	if s.stopped || !time.Now().Before(s.deadline) {
		s.mu.Unlock()
		return errors.New("session expired during command")
	}
	// A successful FRAME is the heartbeat. NEUTRAL cannot keep a silent feeder alive.
	if !neutral {
		s.deadline = time.Now().Add(s.limits.heartbeat)
	}
	s.mu.Unlock()
	select {
	case s.wake <- struct{}{}:
	default:
	}
	return nil
}

func (s *session) close() error {
	s.cleanupOnce.Do(func() {
		s.mu.Lock()
		s.stopped = true
		s.active = false
		b := s.backend
		s.mu.Unlock()
		s.cancel()
		if b != nil {
			// Each attempt is bounded. Close still runs if neutralization fails.
			neutralLimit, removalLimit := s.limits.cleanup/3, s.limits.cleanup*2/3
			if s.limits.neutralCleanup > 0 {
				neutralLimit = s.limits.neutralCleanup
			}
			if s.limits.removalCleanup > 0 {
				removalLimit = s.limits.removalCleanup
			}
			neutralErr := bounded(context.Background(), neutralLimit, b.Neutral)
			closeErr := bounded(context.Background(), removalLimit, b.Close)
			s.cleanupErr = errors.Join(neutralErr, closeErr)
		}
		close(s.cleanupDone)
	})
	<-s.cleanupDone
	return s.cleanupErr
}

type inputEvent struct {
	line string
	err  error
}

func readBounded(reader *bufio.Reader) (string, error) {
	var b strings.Builder
	for {
		ch, err := reader.ReadByte()
		if err != nil {
			if err == io.EOF && b.Len() != 0 {
				return "", errors.New("unterminated command")
			}
			return "", err
		}
		if ch == '\n' {
			return strings.TrimSuffix(b.String(), "\r"), nil
		}
		if b.Len() == maxCommand {
			return "", errors.New("command too long")
		}
		if ch > 127 || ch == 0 {
			return "", errors.New("command must be ASCII")
		}
		b.WriteByte(ch)
	}
}

func parseCommand(line string, previous uint64) (uint64, string, packet, error) {
	f := strings.Split(line, " ")
	var p packet
	if len(f) < 3 || f[0] != wireVersion {
		return 0, "", p, errors.New("invalid protocol version")
	}
	if f[1] == "" || strings.IndexFunc(f[1], func(r rune) bool { return r < '0' || r > '9' }) >= 0 {
		return 0, "", p, errors.New("invalid command id")
	}
	id, err := strconv.ParseUint(f[1], 10, 63)
	if err != nil || id <= previous {
		return id, "", p, errors.New("command id must increase")
	}
	op := f[2]
	if op != "FRAME" {
		if len(f) != 3 {
			return id, op, p, errors.New("invalid command length")
		}
		return id, op, p, nil
	}
	if len(f) != 10 {
		return id, op, p, errors.New("invalid frame length")
	}
	values := make([]int64, 7)
	for i := range values {
		value, err := strconv.ParseInt(f[i+3], 10, 32)
		if err != nil {
			return id, op, p, errors.New("invalid integer")
		}
		min, max := int64(-32768), int64(32767)
		if i == 0 {
			min, max = 0, 65535
		}
		if i == 1 || i == 2 {
			min, max = 0, 255
		}
		if value < min || value > max {
			return id, op, p, errors.New("frame value out of range")
		}
		values[i] = value
	}
	if values[0]&^0xF3FF != 0 {
		return id, op, p, errors.New("reserved buttons prohibited")
	}
	p = packet{uint16(values[0]), uint8(values[1]), uint8(values[2]), int16(values[3]), int16(values[4]), int16(values[5]), int16(values[6])}
	return id, op, p, nil
}

func errorReply(err error) string {
	message := strings.ReplaceAll(strings.ReplaceAll(err.Error(), "\r", " "), "\n", " ")
	r := []rune(message)
	if len(r) > 256 {
		message = string(r[:256])
	}
	return base64.StdEncoding.EncodeToString([]byte(message))
}

func runHost(input io.Reader, output io.Writer, factory func() outputBackend, limits hostLimits) int {
	s := newSession(limits)
	defer s.close()
	lines := make(chan inputEvent, 1)
	readerDone := make(chan struct{})
	defer close(readerDone)
	go func() {
		r := bufio.NewReader(input)
		for {
			line, err := readBounded(r)
			event := inputEvent{line, err}
			if err != nil {
				s.mu.Lock()
				active := s.active
				s.mu.Unlock()
				if active {
					s.fail(err)
				} else {
					s.cancel()
				}
			}
			select {
			case lines <- event:
			case <-readerDone:
				return
			}
			if err != nil {
				return
			}
		}
	}()
	var previous uint64
	fail := func(id uint64, err error) int {
		cleanupErr := s.close()
		err = errors.Join(err, cleanupErr)
		_, _ = fmt.Fprintf(output, "%s %d ERROR %s\n", wireVersion, id, errorReply(err))
		return 1
	}
	for {
		select {
		case err := <-s.fault:
			if errors.Is(err, io.EOF) {
				if cleanupErr := s.close(); cleanupErr != nil {
					return fail(previous, cleanupErr)
				}
				return 0
			}
			return fail(previous, err)
		case item := <-lines:
			if item.err != nil {
				if errors.Is(item.err, io.EOF) {
					if err := s.close(); err != nil {
						return fail(previous, err)
					}
					return 0
				}
				return fail(previous, item.err)
			}
			id, op, p, err := parseCommand(item.line, previous)
			if err != nil {
				return fail(id, err)
			}
			previous = id
			switch op {
			case "CONNECT":
				err = s.connect(factory)
			case "FRAME":
				err = s.submit(p, false)
			case "NEUTRAL":
				err = s.submit(packet{}, true)
			case "QUIT":
				err = s.close()
			default:
				err = errors.New("unknown command")
			}
			if err != nil {
				return fail(id, err)
			}
			if _, err = fmt.Fprintf(output, "%s %d ACK\n", wireVersion, id); err != nil {
				return fail(id, err)
			}
			if op == "QUIT" {
				return 0
			}
		}
	}
}
