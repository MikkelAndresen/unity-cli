//go:build windows

package client

import (
	"syscall"
	"unsafe"
)

var (
	modkernel32            = syscall.NewLazyDLL("kernel32.dll")
	procOpenProcess        = modkernel32.NewProc("OpenProcess")
	procGetExitCodeProcess = modkernel32.NewProc("GetExitCodeProcess")

	procCreateToolhelp32Snapshot = modkernel32.NewProc("CreateToolhelp32Snapshot")
	procProcess32FirstW          = modkernel32.NewProc("Process32FirstW")
	procProcess32NextW           = modkernel32.NewProc("Process32NextW")
)

const (
	processQueryLimitedInfo = 0x1000
	stillActive             = 259

	th32SnapProcess = 0x00000002
	maxPathW        = 260
)

// processEntry32W mirrors the SDK's PROCESSENTRY32W. The field order and widths are the API's, and
// the uintptr for th32DefaultHeapID (a ULONG_PTR) gives the same alignment padding C gives on both
// amd64 and arm64, so no explicit padding is needed.
type processEntry32W struct {
	size        uint32
	usage       uint32
	pid         uint32
	defaultHeap uintptr
	moduleID    uint32
	threads     uint32
	parentPID   uint32
	priority    int32
	flags       uint32
	exeFile     [maxPathW]uint16
}

// checkProcessDead returns true only when the process is confirmed to not exist.
// Returns false for access-denied or transient errors, preserving the instance file.
func checkProcessDead(pid int) bool {
	handle, _, err := procOpenProcess.Call(
		uintptr(processQueryLimitedInfo),
		0,
		uintptr(pid),
	)
	if handle == 0 {
		// ACCESS_DENIED means the process exists but we lack permission
		if errno, ok := err.(syscall.Errno); ok && errno == syscall.ERROR_ACCESS_DENIED {
			return false
		}
		// Other errors (e.g. ERROR_INVALID_PARAMETER) — process does not exist
		return true
	}
	defer func() {
		_ = syscall.CloseHandle(syscall.Handle(handle))
	}()

	var exitCode uint32
	ret, _, _ := procGetExitCodeProcess.Call(handle, uintptr(unsafe.Pointer(&exitCode)))
	if ret == 0 {
		// Could not query exit code — be conservative
		return false
	}
	return exitCode != stillActive
}

// readProcessTable lists the processes this machine can see, as pid -> entry, or nil when the
// snapshot could not be taken. nil is deliberately not the same as an empty map: judgePID may treat
// a missing row as proof of death only when the table itself was readable. Windows has no zombie
// state: an exited process is gone from the snapshot outright.
func readProcessTable() map[int]ProcessEntry {
	handle, _, err := procCreateToolhelp32Snapshot.Call(th32SnapProcess, 0)
	if err != nil || handle == 0 || handle == ^uintptr(0) {
		return nil
	}
	defer syscall.CloseHandle(syscall.Handle(handle))

	entry := processEntry32W{}
	entry.size = uint32(unsafe.Sizeof(entry))

	ret, _, err := procProcess32FirstW.Call(handle, uintptr(unsafe.Pointer(&entry)))
	if ret == 0 {
		return nil
	}

	table := map[int]ProcessEntry{}
	for {
		if entry.pid != 0 {
			table[int(entry.pid)] = ProcessEntry{Name: utf16ToString(entry.exeFile[:])}
		}
		// ret == 0 ends the walk, normally with ERROR_NO_MORE_FILES. Whatever was collected before the
		// walk stopped is still a table the caller can reason about.
		if ret, _, _ = procProcess32NextW.Call(handle, uintptr(unsafe.Pointer(&entry))); ret == 0 {
			break
		}
	}
	if len(table) == 0 {
		return nil
	}
	return table
}

// utf16ToString trims a UTF-16 path at its NUL terminator.
func utf16ToString(buf []uint16) string {
	if idx := indexOfNUL(buf); idx >= 0 {
		buf = buf[:idx]
	}
	return syscall.UTF16ToString(buf)
}

func indexOfNUL(buf []uint16) int {
	for i, v := range buf {
		if v == 0 {
			return i
		}
	}
	return -1
}
