# =============================================================================
# BINDER Climate Chamber Control v3.4
# COMMENTED / STUDY EDITION
# =============================================================================
# This application communicates with a BINDER MB2 chamber over:
#
#       Ethernet TCP -> port 10001 -> raw BINDER Modbus tunnel
#
# Important source used for the MB2 register map:
# BINDER Interface Technical Specifications, issue 10/2017.
#
# LEARN:  Comments beginning with LEARN explain Python/programming concepts.
# BINDER: Comments beginning with BINDER explain controller/protocol behavior.
# SAFETY: Comments beginning with SAFETY identify code that can change the
#         physical chamber.
#
# v3.4 adds:
# - Read/write MB2 Track manual register 0x1158 (16-bit Functions/Tracks).
# - Read MB2 Track read register 0x1292.
# - 16 checkbox UI for track bits.
# - Read-back verification after Track writes.
# - Program number 0x1147, start section 0x1148, START 0x1149,
#   STOP 0x114A, PAUSE 0x114B based on the published MB2 table.
# - Program START is fail-safe: if program number or start-section write/read-back
#   fails, the application DOES NOT send the START command.
#
# NOTE ABOUT START SECTION:
# The 2017 interface table names 0x1148 "Start section number" but does not state
# whether that field is zero-based or one-based. Therefore this application sends
# exactly the raw integer entered by the user. It does not silently add/subtract 1.
# =============================================================================

import socket       # LEARN: TCP/IP sockets; used to connect to 192.168.3.137:10001.
import struct       # LEARN: converts Python float values to/from raw IEEE-754 bytes.
import threading    # LEARN: keeps network/ramp/sequence work from freezing the GUI.
import time
import csv
import json
import re
import math         # LEARN: numeric checks used by the live chart.
from pathlib import Path
from datetime import datetime
import tkinter as tk
from tkinter import ttk, filedialog, messagebox

# BINDER MB2 addresses — BINDER Interface Technical Specifications 10/2017
REG_TEMP_ACT = 0x1004
REG_HUM_ACT  = 0x100A
REG_TEMP_SP_READ = 0x10B2
REG_HUM_SP_READ  = 0x10B4
REG_FAN_SP_READ  = 0x10B6

REG_TEMP_SP_MANUAL = 0x114C
REG_HUM_SP_MANUAL  = 0x114E
REG_FAN_SP_MANUAL  = 0x1150

# BINDER: MB2 program control registers, page 27 of the 10/2017 interface spec.
REG_PROGRAM_NUMBER = 0x1147
REG_START_SECTION  = 0x1148
REG_START_PROGRAM  = 0x1149
REG_STOP_PROGRAM   = 0x114A
REG_PAUSE_PROGRAM  = 0x114B

# BINDER: "Track" is a 16-bit field. The interface specification states:
# Track read   0x1292, R/O, bits 0..15 = Track 0..15
# Track manual 0x1158, R/W, bits 0..15 = Track 0..15
REG_TRACK_READ     = 0x1292
REG_TRACK_MANUAL   = 0x1158

DEFAULT_PORT = 10001
DISCOVERY_PORT = 30718
SLAVE_ID = 1

def crc16_modbus(data: bytes) -> int:
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if (crc & 1) else (crc >> 1)
    return crc & 0xFFFF

def frame_with_crc(payload: bytes) -> bytes:
    crc = crc16_modbus(payload)
    return payload + bytes([crc & 0xFF, (crc >> 8) & 0xFF])

def recv_exact(sock, n):
    data = b""
    while len(data) < n:
        chunk = sock.recv(n - len(data))
        if not chunk:
            raise ConnectionError("Connection closed by chamber")
        data += chunk
    return data

def verify_crc(frame: bytes):
    if len(frame) < 4:
        raise ValueError("Short response")
    calc = crc16_modbus(frame[:-2])
    got = frame[-2] | (frame[-1] << 8)
    if calc != got:
        raise ValueError("CRC error")

def read_words(sock, lock, address, words):
    payload = bytes([
        SLAVE_ID, 0x03,
        (address >> 8) & 0xFF, address & 0xFF,
        (words >> 8) & 0xFF, words & 0xFF
    ])
    with lock:
        sock.sendall(frame_with_crc(payload))
        header = recv_exact(sock, 3)
        slave, func, third = header
        if func & 0x80:
            rest = recv_exact(sock, 2)
            frame = header + rest
            verify_crc(frame)
            raise ValueError(f"Modbus exception code {third}")
        byte_count = third
        rest = recv_exact(sock, byte_count + 2)
        frame = header + rest
    verify_crc(frame)
    if slave != SLAVE_ID or func != 0x03:
        raise ValueError("Unexpected Modbus response")
    return frame[3:3+byte_count]

def binder_float_from_bytes(b):
    if len(b) != 4:
        raise ValueError("Float requires 4 bytes")
    # BINDER: 16-bit word swapped relative to IEEE byte sequence.
    ieee = bytes([b[2], b[3], b[0], b[1]])
    return struct.unpack(">f", ieee)[0]

def binder_float_to_bytes(value):
    ieee = struct.pack(">f", float(value))
    return bytes([ieee[2], ieee[3], ieee[0], ieee[1]])

def read_float(sock, lock, address):
    """Read a 32-bit BINDER float (two consecutive 16-bit registers)."""
    return binder_float_from_bytes(read_words(sock, lock, address, 2))


def read_word(sock, lock, address):
    """Read one unsigned 16-bit Modbus register.

    LEARN: read_words(..., 1) returns exactly two bytes.
    We combine the high and low byte into one integer.
    """
    data = read_words(sock, lock, address, 1)
    if len(data) != 2:
        raise ValueError("Expected two bytes for 16-bit word")
    return (data[0] << 8) | data[1]

def write_single_word(sock, lock, address, value):
    value = int(value) & 0xFFFF
    payload = bytes([
        SLAVE_ID, 0x06,
        (address >> 8) & 0xFF, address & 0xFF,
        (value >> 8) & 0xFF, value & 0xFF
    ])
    req = frame_with_crc(payload)
    with lock:
        sock.sendall(req)
        resp = recv_exact(sock, 8)
    verify_crc(resp)
    if resp[:6] != req[:6]:
        if resp[1] & 0x80:
            raise ValueError(f"Modbus exception code {resp[2]}")
        raise ValueError("Unexpected write response")
    return True

def write_float(sock, lock, address, value):
    data = binder_float_to_bytes(value)
    payload = bytes([
        SLAVE_ID, 0x10,
        (address >> 8) & 0xFF, address & 0xFF,
        0x00, 0x02, 0x04
    ]) + data
    req = frame_with_crc(payload)
    with lock:
        sock.sendall(req)
        resp = recv_exact(sock, 8)
    verify_crc(resp)
    if resp[0] != SLAVE_ID:
        raise ValueError("Unexpected slave address")
    if resp[1] & 0x80:
        raise ValueError(f"Modbus exception code {resp[2]}")
    if resp[1] not in (0x06, 0x10):
        raise ValueError(f"Unexpected write function 0x{resp[1]:02X}")
    return True

class BinderApp:
    def __init__(self, root):
        self.root = root
        self.root.title("BINDER Climate Chamber Control v3.4 — Functions + Verified Program Control")
        self.root.geometry("1220x900")
        self.root.minsize(1080, 780)

        self.sock = None
        self.io_lock = threading.Lock()
        self.polling = False
        self.worker = None
        self.log_file = None
        self.log_writer = None
        self.history_t, self.history_temp, self.history_hum = [], [], []
        self.max_points = 300

        self.ip_var = tk.StringVar(value="192.168.3.137")
        self.port_var = tk.StringVar(value=str(DEFAULT_PORT))
        self.status_var = tk.StringVar(value="Disconnected")
        self.temp_var = tk.StringVar(value="--.- °C")
        self.hum_var = tk.StringVar(value="--.- %RH")
        self.temp_sp_var = tk.StringVar(value="--.- °C")
        self.hum_sp_var = tk.StringVar(value="--.- %RH")
        self.fan_sp_var = tk.StringVar(value="--.- %")
        self.log_status_var = tk.StringVar(value="Logging: OFF")
        self.interval_var = tk.StringVar(value="2")
        self.model_var = tk.StringVar(value="MKF")
        self.temp_new_var = tk.StringVar()
        self.hum_new_var = tk.StringVar()
        self.fan_new_var = tk.StringVar()
        self.program_var = tk.StringVar(value="1")
        self.section_var = tk.StringVar(value="1")
        self.temp_mode_var = tk.StringVar(value="STEP")
        self.hum_mode_var = tk.StringVar(value="STEP")
        self.temp_ramp_minutes_var = tk.StringVar(value="30")
        self.hum_ramp_minutes_var = tk.StringVar(value="30")
        self.ramp_interval_var = tk.StringVar(value="10")
        self.temp_ramp_cancel = threading.Event()
        self.hum_ramp_cancel = threading.Event()
        self.write_enabled = tk.BooleanVar(value=False)
        self.program_names_path = Path(__file__).with_name("program_names.json")
        self.program_names = self.load_program_names()
        self.program_choice_var = tk.StringVar(value=self.program_display_name(1))
        self.program_name_edit_var = tk.StringVar(value=self.program_names.get("1", ""))
        self.protocol_log = []

        # ---------------------------------------------------------------------
        # MB2 Functions / Track state
        # ---------------------------------------------------------------------
        # BINDER: Track manual is a 16-bit value. Each BooleanVar corresponds
        # to one bit. Bit 0 is the right-most bit when shown as 16 binary digits.
        self.track_bits = [tk.BooleanVar(value=False) for _ in range(16)]
        self.track_manual_value_var = tk.StringVar(value="0x0000 / 0000000000000000")
        self.track_read_value_var = tk.StringVar(value="0x0000 / 0000000000000000")
        self.track_status_var = tk.StringVar(value="Functions/Tracks: not read yet")

        # Program-control safety gate is separate from normal manual setpoint
        # enable. This prevents an accidental program start.
        self.program_control_enabled = tk.BooleanVar(value=False)
        self.program_status_var = tk.StringVar(value="Program control: IDLE")

        # PC-side sequence controller state.
        self.sequence_rows = []
        self.sequence_running = False
        self.sequence_paused = False
        self.sequence_stop_event = threading.Event()
        self.sequence_pause_event = threading.Event()
        self.sequence_thread = None
        self.sequence_status_var = tk.StringVar(value="Sequence: IDLE")
        self.sequence_elapsed_var = tk.StringVar(value="Elapsed: 00:00:00")
        self.sequence_remaining_var = tk.StringVar(value="Remaining: --")
        self.sequence_current_var = tk.StringVar(value="Current section: --")
        self.seq_duration_var = tk.StringVar(value="30")
        self.seq_temp_var = tk.StringVar(value="25")
        self.seq_temp_mode_var = tk.StringVar(value="STEP")
        self.seq_hum_var = tk.StringVar(value="50")
        self.seq_hum_mode_var = tk.StringVar(value="STEP")
        self.seq_ramp_interval_var = tk.StringVar(value="10")

        self.build_ui()
        self.root.protocol("WM_DELETE_WINDOW", self.on_close)
        self.root.after(300, self.draw_chart)

    def load_program_names(self):
        try:
            if self.program_names_path.exists():
                data = json.loads(self.program_names_path.read_text(encoding="utf-8"))
                if isinstance(data, dict):
                    return {str(k): str(v) for k, v in data.items()}
        except Exception:
            pass
        return {}

    def save_program_names(self):
        try:
            self.program_names_path.write_text(
                json.dumps(self.program_names, ensure_ascii=False, indent=2),
                encoding="utf-8"
            )
        except Exception as e:
            messagebox.showerror("Program Manager", f"Could not save names:\n{e}")

    def program_display_name(self, n):
        n = int(n)
        base = f"Time Program {n}" if n <= 25 else f"Week Program {n}"
        custom = self.program_names.get(str(n), "").strip()
        return f"{base} — {custom}" if custom else base

    def refresh_program_choices(self, select_number=None):
        values = [self.program_display_name(i) for i in range(1, 31)]
        if hasattr(self, "program_combo"):
            self.program_combo["values"] = values
        if select_number is not None:
            self.program_choice_var.set(self.program_display_name(select_number))
            self.program_var.set(str(select_number))
            self.program_name_edit_var.set(self.program_names.get(str(select_number), ""))

    def selected_program_number(self):
        s = self.program_choice_var.get().strip()
        m = re.search(r"(?:Time|Week) Program\s+(\d+)", s)
        if m:
            return int(m.group(1))
        try:
            return int(self.program_var.get())
        except Exception:
            return 1

    def on_program_selected(self, event=None):
        n = self.selected_program_number()
        self.program_var.set(str(n))
        self.program_name_edit_var.set(self.program_names.get(str(n), ""))

    def save_current_program_name(self):
        n = self.selected_program_number()
        name = self.program_name_edit_var.get().strip()
        if name:
            self.program_names[str(n)] = name
        else:
            self.program_names.pop(str(n), None)
        self.save_program_names()
        self.refresh_program_choices(select_number=n)
        self.status_var.set(f"Saved local name for Program {n}")

    def add_protocol_log(self, line):
        ts = datetime.now().strftime("%H:%M:%S.%f")[:-3]
        entry = f"{ts}  {line}"
        self.protocol_log.append(entry)
        self.protocol_log = self.protocol_log[-500:]
        if hasattr(self, "log_text"):
            try:
                self.log_text.config(state="normal")
                self.log_text.insert("end", entry + "\n")
                self.log_text.see("end")
                self.log_text.config(state="disabled")
            except Exception:
                pass

    def clear_protocol_log(self):
        self.protocol_log.clear()
        if hasattr(self, "log_text"):
            self.log_text.config(state="normal")
            self.log_text.delete("1.0", "end")
            self.log_text.config(state="disabled")

    def save_protocol_log(self):
        path = filedialog.asksaveasfilename(
            defaultextension=".txt",
            filetypes=[("Text files","*.txt")],
            initialfile=f"BINDER_protocol_{datetime.now().strftime('%Y%m%d_%H%M%S')}.txt"
        )
        if not path:
            return
        Path(path).write_text("\n".join(self.protocol_log), encoding="utf-8")

    def hexs(self, b):
        return " ".join(f"{x:02X}" for x in b)

    def sequence_total_seconds(self):
        total = 0.0
        for row in self.sequence_rows:
            total += float(row["duration_min"]) * 60.0
        return total

    def fmt_seconds(self, seconds):
        seconds = max(0, int(round(seconds)))
        h = seconds // 3600
        m = (seconds % 3600) // 60
        s = seconds % 60
        return f"{h:02d}:{m:02d}:{s:02d}"

    def refresh_sequence_table(self):
        if not hasattr(self, "sequence_tree"):
            return
        for item in self.sequence_tree.get_children():
            self.sequence_tree.delete(item)
        for i, row in enumerate(self.sequence_rows, start=1):
            self.sequence_tree.insert(
                "", "end",
                values=(
                    i,
                    f'{float(row["duration_min"]):g}',
                    f'{float(row["temp"]):g}',
                    row["temp_mode"],
                    f'{float(row["hum"]):g}',
                    row["hum_mode"],
                )
            )
        self.sequence_remaining_var.set(
            "Remaining: " + (self.fmt_seconds(self.sequence_total_seconds()) if self.sequence_rows else "--")
        )

    def add_sequence_section(self):
        try:
            duration = float(self.seq_duration_var.get())
            temp = float(self.seq_temp_var.get())
            hum = float(self.seq_hum_var.get())
        except Exception:
            messagebox.showerror("Sequence", "Duration, temperature, and humidity must be numeric.")
            return

        if duration <= 0:
            messagebox.showerror("Sequence", "Section duration must be greater than 0 minutes.")
            return

        lo = -50 if self.model_var.get() == "MKF" else -80
        if not (lo <= temp <= 180):
            messagebox.showerror("Sequence", f"Temperature must be between {lo} and 180 °C for the selected model.")
            return
        if not (0 <= hum <= 100):
            messagebox.showerror("Sequence", "Humidity must be between 0 and 100 %RH.")
            return

        self.sequence_rows.append({
            "duration_min": duration,
            "temp": temp,
            "temp_mode": self.seq_temp_mode_var.get(),
            "hum": hum,
            "hum_mode": self.seq_hum_mode_var.get(),
        })
        self.refresh_sequence_table()

    def delete_sequence_section(self):
        sel = self.sequence_tree.selection()
        if not sel:
            return
        indices = sorted(
            [self.sequence_tree.index(item) for item in sel],
            reverse=True
        )
        for idx in indices:
            if 0 <= idx < len(self.sequence_rows):
                self.sequence_rows.pop(idx)
        self.refresh_sequence_table()

    def move_sequence_section(self, direction):
        sel = self.sequence_tree.selection()
        if len(sel) != 1:
            return
        idx = self.sequence_tree.index(sel[0])
        new_idx = idx + direction
        if new_idx < 0 or new_idx >= len(self.sequence_rows):
            return
        self.sequence_rows[idx], self.sequence_rows[new_idx] = self.sequence_rows[new_idx], self.sequence_rows[idx]
        self.refresh_sequence_table()
        children = self.sequence_tree.get_children()
        self.sequence_tree.selection_set(children[new_idx])
        self.sequence_tree.see(children[new_idx])

    def clear_sequence(self):
        if self.sequence_running:
            messagebox.showwarning("Sequence", "Stop the running sequence before clearing it.")
            return
        if self.sequence_rows and not messagebox.askyesno("Sequence", "Clear all sequence sections?"):
            return
        self.sequence_rows.clear()
        self.refresh_sequence_table()

    def save_sequence(self):
        if not self.sequence_rows:
            messagebox.showinfo("Sequence", "There is no sequence to save.")
            return
        path = filedialog.asksaveasfilename(
            defaultextension=".json",
            filetypes=[("BINDER sequence", "*.json"), ("JSON", "*.json")],
            initialfile=f"BINDER_sequence_{datetime.now().strftime('%Y%m%d_%H%M%S')}.json"
        )
        if not path:
            return
        payload = {
            "format": "BINDER_PC_SEQUENCE_v1",
            "model": self.model_var.get(),
            "ramp_update_interval_s": float(self.seq_ramp_interval_var.get()),
            "sections": self.sequence_rows,
        }
        Path(path).write_text(json.dumps(payload, indent=2), encoding="utf-8")

    def load_sequence(self):
        if self.sequence_running:
            messagebox.showwarning("Sequence", "Stop the running sequence before loading another sequence.")
            return
        path = filedialog.askopenfilename(
            filetypes=[("BINDER sequence", "*.json"), ("JSON", "*.json")]
        )
        if not path:
            return
        try:
            payload = json.loads(Path(path).read_text(encoding="utf-8"))
            sections = payload.get("sections", [])
            if not isinstance(sections, list):
                raise ValueError("Invalid sequence file")
            clean = []
            for row in sections:
                clean.append({
                    "duration_min": float(row["duration_min"]),
                    "temp": float(row["temp"]),
                    "temp_mode": str(row["temp_mode"]).upper(),
                    "hum": float(row["hum"]),
                    "hum_mode": str(row["hum_mode"]).upper(),
                })
            self.sequence_rows = clean
            if "ramp_update_interval_s" in payload:
                self.seq_ramp_interval_var.set(str(payload["ramp_update_interval_s"]))
            self.refresh_sequence_table()
        except Exception as e:
            messagebox.showerror("Sequence", f"Could not load sequence:\n{e}")

    def wait_sequence(self, seconds):
        end = time.time() + seconds
        while time.time() < end:
            if self.sequence_stop_event.is_set():
                raise RuntimeError("Sequence stopped by user")
            while self.sequence_pause_event.is_set():
                self.root.after(0, lambda: self.sequence_status_var.set("Sequence: PAUSED"))
                time.sleep(0.2)
                if self.sequence_stop_event.is_set():
                    raise RuntimeError("Sequence stopped by user")
            time.sleep(min(0.2, max(0.01, end - time.time())))

    def sequence_write_pair_step(self, temp, hum):
        write_float(self.sock, self.io_lock, REG_TEMP_SP_MANUAL, temp)
        write_float(self.sock, self.io_lock, REG_HUM_SP_MANUAL, hum)

    def start_sequence(self):
        if self.sequence_running:
            messagebox.showinfo("Sequence", "A sequence is already running.")
            return
        if not self.require_connected_and_enabled():
            return
        if not self.sequence_rows:
            messagebox.showerror("Sequence", "Add at least one section first.")
            return
        try:
            ramp_interval = float(self.seq_ramp_interval_var.get())
        except Exception:
            messagebox.showerror("Sequence", "Ramp update interval must be numeric.")
            return
        if ramp_interval < 1:
            messagebox.showerror("Sequence", "Ramp update interval must be at least 1 second.")
            return

        summary = "\n".join(
            f'{i+1}. {r["duration_min"]:g} min | T {r["temp"]:g} °C {r["temp_mode"]} | '
            f'H {r["hum"]:g} %RH {r["hum_mode"]}'
            for i, r in enumerate(self.sequence_rows)
        )
        if not messagebox.askyesno(
            "Start PC Sequence",
            "This sequence will automatically change the REAL chamber setpoints from this laptop.\n\n"
            + summary
            + "\n\nThe laptop, application, and Ethernet connection must remain active.\n\nStart now?"
        ):
            return

        self.sequence_stop_event.clear()
        self.sequence_pause_event.clear()
        self.sequence_running = True
        self.sequence_paused = False
        self.sequence_status_var.set("Sequence: RUNNING")
        self.sequence_start_btn.config(state="disabled")
        self.sequence_pause_btn.config(state="normal")
        self.sequence_stop_btn.config(state="normal")

        rows = [dict(r) for r in self.sequence_rows]

        def worker():
            seq_start = time.time()
            try:
                current_temp = read_float(self.sock, self.io_lock, REG_TEMP_SP_READ)
                current_hum = read_float(self.sock, self.io_lock, REG_HUM_SP_READ)
                total_remaining = sum(float(r["duration_min"]) * 60 for r in rows)

                for idx, row in enumerate(rows):
                    if self.sequence_stop_event.is_set():
                        raise RuntimeError("Sequence stopped by user")

                    duration_s = float(row["duration_min"]) * 60.0
                    target_temp = float(row["temp"])
                    target_hum = float(row["hum"])
                    tmode = row["temp_mode"]
                    hmode = row["hum_mode"]

                    self.root.after(
                        0,
                        lambda idx=idx, total=len(rows): self.sequence_current_var.set(
                            f"Current section: {idx+1}/{total}"
                        )
                    )
                    self.add_protocol_log(
                        f'--- PC SEQUENCE section {idx+1}/{len(rows)} | '
                        f'{duration_s/60:g} min | T {current_temp:.2f}->{target_temp:.2f} {tmode} | '
                        f'H {current_hum:.2f}->{target_hum:.2f} {hmode} ---'
                    )

                    # STEP channels are applied immediately at section start.
                    if tmode == "STEP":
                        write_float(self.sock, self.io_lock, REG_TEMP_SP_MANUAL, target_temp)
                    if hmode == "STEP":
                        write_float(self.sock, self.io_lock, REG_HUM_SP_MANUAL, target_hum)

                    section_start = time.time()
                    steps = max(1, int(duration_s / ramp_interval))

                    for step in range(steps + 1):
                        if self.sequence_stop_event.is_set():
                            raise RuntimeError("Sequence stopped by user")

                        while self.sequence_pause_event.is_set():
                            self.root.after(0, lambda: self.sequence_status_var.set("Sequence: PAUSED"))
                            time.sleep(0.2)
                            if self.sequence_stop_event.is_set():
                                raise RuntimeError("Sequence stopped by user")

                        frac = min(1.0, step / steps)
                        if step > 0:
                            if tmode == "RAMP":
                                tset = current_temp + (target_temp - current_temp) * frac
                                write_float(self.sock, self.io_lock, REG_TEMP_SP_MANUAL, tset)
                            if hmode == "RAMP":
                                hset = current_hum + (target_hum - current_hum) * frac
                                write_float(self.sock, self.io_lock, REG_HUM_SP_MANUAL, hset)

                        elapsed = time.time() - seq_start
                        section_elapsed = min(duration_s, time.time() - section_start)
                        remaining = max(0, total_remaining - section_elapsed)
                        self.root.after(
                            0,
                            lambda elapsed=elapsed, remaining=remaining: (
                                self.sequence_elapsed_var.set("Elapsed: " + self.fmt_seconds(elapsed)),
                                self.sequence_remaining_var.set("Remaining: " + self.fmt_seconds(remaining)),
                                self.sequence_status_var.set("Sequence: RUNNING"),
                            )
                        )

                        if step < steps:
                            self.wait_sequence(min(ramp_interval, max(0.0, duration_s - section_elapsed)))

                    # Force exact final target for both channels.
                    write_float(self.sock, self.io_lock, REG_TEMP_SP_MANUAL, target_temp)
                    write_float(self.sock, self.io_lock, REG_HUM_SP_MANUAL, target_hum)
                    current_temp, current_hum = target_temp, target_hum
                    total_remaining -= duration_s

                self.add_protocol_log("--- PC SEQUENCE COMPLETE ---")
                self.root.after(0, self.sequence_finished)

            except Exception as e:
                self.add_protocol_log(f"--- PC SEQUENCE STOPPED/FAILED: {e} ---")
                self.root.after(0, lambda err=str(e): self.sequence_failed(err))

        self.sequence_thread = threading.Thread(target=worker, daemon=True)
        self.sequence_thread.start()

    def pause_sequence(self):
        if not self.sequence_running:
            return
        if self.sequence_pause_event.is_set():
            self.sequence_pause_event.clear()
            self.sequence_paused = False
            self.sequence_pause_btn.config(text="Pause Sequence")
            self.sequence_status_var.set("Sequence: RUNNING")
        else:
            self.sequence_pause_event.set()
            self.sequence_paused = True
            self.sequence_pause_btn.config(text="Resume Sequence")
            self.sequence_status_var.set("Sequence: PAUSED")

    def stop_sequence(self):
        if not self.sequence_running:
            return
        if messagebox.askyesno(
            "Stop PC Sequence",
            "Stop automatic sequence execution?\n\nThe chamber will remain at the most recently transmitted setpoints."
        ):
            self.sequence_stop_event.set()
            self.sequence_pause_event.clear()

    def sequence_finished(self):
        self.sequence_running = False
        self.sequence_paused = False
        self.sequence_start_btn.config(state="normal")
        self.sequence_pause_btn.config(state="disabled", text="Pause Sequence")
        self.sequence_stop_btn.config(state="disabled")
        self.sequence_status_var.set("Sequence: COMPLETE")
        self.sequence_current_var.set("Current section: --")
        self.sequence_remaining_var.set("Remaining: 00:00:00")
        messagebox.showinfo("PC Sequence", "Sequence completed successfully.")

    def sequence_failed(self, err):
        self.sequence_running = False
        self.sequence_paused = False
        self.sequence_start_btn.config(state="normal")
        self.sequence_pause_btn.config(state="disabled", text="Pause Sequence")
        self.sequence_stop_btn.config(state="disabled")
        if "stopped by user" in err.lower():
            self.sequence_status_var.set("Sequence: STOPPED")
        else:
            self.sequence_status_var.set("Sequence: FAILED")
            messagebox.showerror("PC Sequence", err)

    def build_ui(self):
        pad = {"padx": 7, "pady": 5}

        # Top connection bar is always visible.
        conn = ttk.LabelFrame(self.root, text="Connection")
        conn.pack(fill="x", padx=12, pady=(10,6))

        ttk.Label(conn, text="Chamber IP:").grid(row=0, column=0, **pad)
        ttk.Entry(conn, textvariable=self.ip_var, width=17).grid(row=0, column=1, **pad)
        ttk.Label(conn, text="Port:").grid(row=0, column=2, **pad)
        ttk.Entry(conn, textvariable=self.port_var, width=7).grid(row=0, column=3, **pad)
        ttk.Label(conn, text="Poll (s):").grid(row=0, column=4, **pad)
        ttk.Entry(conn, textvariable=self.interval_var, width=5).grid(row=0, column=5, **pad)
        ttk.Label(conn, text="Model:").grid(row=0, column=6, **pad)
        ttk.Combobox(conn, textvariable=self.model_var, values=["MKF", "MKFT"], state="readonly", width=7).grid(row=0, column=7, **pad)
        ttk.Button(conn, text="Find BINDER", command=self.find_binder).grid(row=0, column=8, **pad)
        self.connect_btn = ttk.Button(conn, text="Connect", command=self.connect)
        self.connect_btn.grid(row=0, column=9, **pad)
        self.disconnect_btn = ttk.Button(conn, text="Disconnect", command=self.disconnect, state="disabled")
        self.disconnect_btn.grid(row=0, column=10, **pad)

        ttk.Label(conn, text="Status:").grid(row=1, column=0, **pad)
        ttk.Label(conn, textvariable=self.status_var).grid(row=1, column=1, columnspan=10, sticky="w", **pad)

        # Main tabs
        self.tabs = ttk.Notebook(self.root)
        self.tabs.pack(fill="both", expand=True, padx=12, pady=(0,10))

        self.tab_live = ttk.Frame(self.tabs)
        self.tab_manual = ttk.Frame(self.tabs)
        self.tab_sequence = ttk.Frame(self.tabs)
        self.tab_functions = ttk.Frame(self.tabs)
        self.tab_program = ttk.Frame(self.tabs)
        self.tab_diag = ttk.Frame(self.tabs)

        self.tabs.add(self.tab_live, text="Live Monitor")
        self.tabs.add(self.tab_manual, text="Manual Control")
        self.tabs.add(self.tab_sequence, text="Sequence Program")
        self.tabs.add(self.tab_functions, text="Functions / Tracks")
        self.tabs.add(self.tab_program, text="Stored Programs")
        self.tabs.add(self.tab_diag, text="Diagnostics")

        # ---------------- LIVE MONITOR ----------------
        vals = ttk.LabelFrame(self.tab_live, text="Live values")
        vals.pack(fill="x", padx=10, pady=(10,6))

        headers = [("Temperature",0),("Humidity",2),("Fan setpoint",4)]
        for txt,col in headers:
            ttk.Label(vals,text=txt,font=("Segoe UI",12,"bold")).grid(row=0,column=col,columnspan=2,**pad)

        ttk.Label(vals,text="Actual:").grid(row=1,column=0,sticky="e",**pad)
        ttk.Label(vals,textvariable=self.temp_var,font=("Segoe UI",20,"bold")).grid(row=1,column=1,sticky="w",**pad)
        ttk.Label(vals,text="Setpoint:").grid(row=2,column=0,sticky="e",**pad)
        ttk.Label(vals,textvariable=self.temp_sp_var,font=("Segoe UI",13)).grid(row=2,column=1,sticky="w",**pad)

        ttk.Label(vals,text="Actual:").grid(row=1,column=2,sticky="e",**pad)
        ttk.Label(vals,textvariable=self.hum_var,font=("Segoe UI",20,"bold")).grid(row=1,column=3,sticky="w",**pad)
        ttk.Label(vals,text="Setpoint:").grid(row=2,column=2,sticky="e",**pad)
        ttk.Label(vals,textvariable=self.hum_sp_var,font=("Segoe UI",13)).grid(row=2,column=3,sticky="w",**pad)

        ttk.Label(vals,text="Read:").grid(row=1,column=4,sticky="e",**pad)
        ttk.Label(vals,textvariable=self.fan_sp_var,font=("Segoe UI",14)).grid(row=1,column=5,sticky="w",**pad)

        for i in range(6):
            vals.columnconfigure(i, weight=1)

        chart_frame = ttk.LabelFrame(self.tab_live, text="Live Trend — Temperature / Humidity")
        chart_frame.pack(fill="both", expand=True, padx=10, pady=6)
        self.canvas = tk.Canvas(chart_frame, bg="white", height=500)
        self.canvas.pack(fill="both", expand=True, padx=8, pady=8)
        self.canvas.bind("<Configure>", lambda e: self.draw_chart())

        live_bottom = ttk.Frame(self.tab_live)
        live_bottom.pack(fill="x", padx=10, pady=(0,10))
        self.log_btn = ttk.Button(live_bottom,text="Start CSV Logging",command=self.toggle_logging)
        self.log_btn.pack(side="left",padx=5)
        ttk.Label(live_bottom,textvariable=self.log_status_var).pack(side="left",padx=8)
        ttk.Button(live_bottom,text="Clear chart",command=self.clear_chart).pack(side="right",padx=5)

        # ---------------- MANUAL CONTROL ----------------
        ctrl = ttk.LabelFrame(self.tab_manual, text="Manual Control — commands change the real chamber")
        ctrl.pack(fill="x", padx=10, pady=(10,6))

        ttk.Checkbutton(
            ctrl,
            text="Enable write controls (I understand commands affect the chamber)",
            variable=self.write_enabled,
            command=self.refresh_write_state
        ).grid(row=0,column=0,columnspan=6,sticky="w",**pad)

        ttk.Label(ctrl,text="New temperature (°C):").grid(row=1,column=0,sticky="e",**pad)
        self.temp_entry = ttk.Entry(ctrl,textvariable=self.temp_new_var,width=10,state="disabled")
        self.temp_entry.grid(row=1,column=1,**pad)
        self.temp_btn = ttk.Button(ctrl,text="Apply Temperature",command=self.set_temperature,state="disabled")
        self.temp_btn.grid(row=1,column=2,**pad)

        temp_mode = ttk.Frame(ctrl)
        temp_mode.grid(row=2,column=0,columnspan=3,sticky="w",padx=7,pady=2)
        ttk.Label(temp_mode,text="Mode:").pack(side="left",padx=(0,5))
        self.temp_step_rb = ttk.Radiobutton(temp_mode,text="STEP",variable=self.temp_mode_var,value="STEP")
        self.temp_step_rb.pack(side="left",padx=3)
        self.temp_ramp_rb = ttk.Radiobutton(temp_mode,text="RAMP",variable=self.temp_mode_var,value="RAMP")
        self.temp_ramp_rb.pack(side="left",padx=3)
        ttk.Label(temp_mode,text="Ramp time (min):").pack(side="left",padx=(10,3))
        self.temp_ramp_entry = ttk.Entry(temp_mode,textvariable=self.temp_ramp_minutes_var,width=7)
        self.temp_ramp_entry.pack(side="left",padx=3)
        self.temp_cancel_btn = ttk.Button(temp_mode,text="Cancel Temp Ramp",command=self.cancel_temp_ramp)
        self.temp_cancel_btn.pack(side="left",padx=(10,0))

        ttk.Label(ctrl,text="New humidity (%RH):").grid(row=1,column=3,sticky="e",**pad)
        self.hum_entry = ttk.Entry(ctrl,textvariable=self.hum_new_var,width=10,state="disabled")
        self.hum_entry.grid(row=1,column=4,**pad)
        self.hum_btn = ttk.Button(ctrl,text="Apply Humidity",command=self.set_humidity,state="disabled")
        self.hum_btn.grid(row=1,column=5,**pad)

        hum_mode = ttk.Frame(ctrl)
        hum_mode.grid(row=2,column=3,columnspan=3,sticky="w",padx=7,pady=2)
        ttk.Label(hum_mode,text="Mode:").pack(side="left",padx=(0,5))
        self.hum_step_rb = ttk.Radiobutton(hum_mode,text="STEP",variable=self.hum_mode_var,value="STEP")
        self.hum_step_rb.pack(side="left",padx=3)
        self.hum_ramp_rb = ttk.Radiobutton(hum_mode,text="RAMP",variable=self.hum_mode_var,value="RAMP")
        self.hum_ramp_rb.pack(side="left",padx=3)
        ttk.Label(hum_mode,text="Ramp time (min):").pack(side="left",padx=(10,3))
        self.hum_ramp_entry = ttk.Entry(hum_mode,textvariable=self.hum_ramp_minutes_var,width=7)
        self.hum_ramp_entry.pack(side="left",padx=3)
        self.hum_cancel_btn = ttk.Button(hum_mode,text="Cancel Hum Ramp",command=self.cancel_hum_ramp)
        self.hum_cancel_btn.pack(side="left",padx=(10,0))

        ttk.Label(ctrl,text="Ramp update interval (s):").grid(row=3,column=0,sticky="e",**pad)
        self.ramp_interval_entry = ttk.Entry(ctrl,textvariable=self.ramp_interval_var,width=7)
        self.ramp_interval_entry.grid(row=3,column=1,sticky="w",**pad)

        ttk.Label(ctrl,text="Fan % (MKF 56 only):").grid(row=4,column=0,sticky="e",**pad)
        self.fan_entry = ttk.Entry(ctrl,textvariable=self.fan_new_var,width=10,state="disabled")
        self.fan_entry.grid(row=4,column=1,**pad)
        self.fan_btn = ttk.Button(ctrl,text="Set Fan",command=self.set_fan,state="disabled")
        self.fan_btn.grid(row=4,column=2,**pad)

        safety = ttk.LabelFrame(self.tab_manual, text="Safety note")
        safety.pack(fill="x", padx=10, pady=6)
        ttk.Label(
            safety,
            text="Manual setpoints and software ramps change the real chamber. "
                 "Stored-program control is on a separate tab and has an independent safety enable. "
                 "For a software RAMP, keep the laptop powered on and connected for the entire ramp.",
            foreground="#8a3b00",
            wraplength=1100
        ).pack(fill="x", padx=10, pady=10)

        # ---------------- SEQUENCE PROGRAM ----------------
        seq = ttk.LabelFrame(self.tab_sequence, text="PC Sequence Controller — automatic setpoint schedule")
        seq.pack(fill="both", expand=True, padx=10, pady=10)

        editor = ttk.LabelFrame(seq, text="Add a section")
        editor.pack(fill="x", padx=8, pady=8)

        ttk.Label(editor, text="Duration (min):").grid(row=0,column=0,padx=4,pady=6)
        ttk.Entry(editor, textvariable=self.seq_duration_var, width=8).grid(row=0,column=1,padx=4,pady=6)
        ttk.Label(editor, text="Temp °C:").grid(row=0,column=2,padx=4,pady=6)
        ttk.Entry(editor, textvariable=self.seq_temp_var, width=8).grid(row=0,column=3,padx=4,pady=6)
        ttk.Label(editor, text="Temp mode:").grid(row=0,column=4,padx=4,pady=6)
        ttk.Combobox(editor, textvariable=self.seq_temp_mode_var, values=["STEP","RAMP"], state="readonly", width=7).grid(row=0,column=5,padx=4,pady=6)
        ttk.Label(editor, text="Humidity %RH:").grid(row=0,column=6,padx=4,pady=6)
        ttk.Entry(editor, textvariable=self.seq_hum_var, width=8).grid(row=0,column=7,padx=4,pady=6)
        ttk.Label(editor, text="Hum mode:").grid(row=0,column=8,padx=4,pady=6)
        ttk.Combobox(editor, textvariable=self.seq_hum_mode_var, values=["STEP","RAMP"], state="readonly", width=7).grid(row=0,column=9,padx=4,pady=6)
        ttk.Button(editor, text="Add Section", command=self.add_sequence_section).grid(row=0,column=10,padx=8,pady=6)

        cols = ("section","duration","temp","tmode","hum","hmode")
        self.sequence_tree = ttk.Treeview(seq, columns=cols, show="headings", height=12, selectmode="extended")
        headings = [
            ("section","Section",65),
            ("duration","Duration min",110),
            ("temp","Temp °C",100),
            ("tmode","Temp mode",100),
            ("hum","Humidity %RH",120),
            ("hmode","Hum mode",100),
        ]
        for key, title, width in headings:
            self.sequence_tree.heading(key, text=title)
            self.sequence_tree.column(key, width=width, anchor="center")
        self.sequence_tree.pack(fill="both", expand=True, padx=8, pady=6)

        seq_buttons = ttk.Frame(seq)
        seq_buttons.pack(fill="x", padx=8, pady=5)
        ttk.Button(seq_buttons, text="Delete Section", command=self.delete_sequence_section).pack(side="left", padx=3)
        ttk.Button(seq_buttons, text="Move Up", command=lambda:self.move_sequence_section(-1)).pack(side="left", padx=3)
        ttk.Button(seq_buttons, text="Move Down", command=lambda:self.move_sequence_section(1)).pack(side="left", padx=3)
        ttk.Button(seq_buttons, text="Clear", command=self.clear_sequence).pack(side="left", padx=3)
        ttk.Button(seq_buttons, text="Save Sequence", command=self.save_sequence).pack(side="left", padx=12)
        ttk.Button(seq_buttons, text="Load Sequence", command=self.load_sequence).pack(side="left", padx=3)
        ttk.Label(seq_buttons, text="Ramp update (s):").pack(side="left", padx=(18,3))
        ttk.Entry(seq_buttons, textvariable=self.seq_ramp_interval_var, width=7).pack(side="left", padx=3)

        run = ttk.LabelFrame(seq, text="Run")
        run.pack(fill="x", padx=8, pady=8)

        self.sequence_start_btn = ttk.Button(run, text="Start Sequence", command=self.start_sequence)
        self.sequence_start_btn.grid(row=0,column=0,padx=5,pady=8)
        self.sequence_pause_btn = ttk.Button(run, text="Pause Sequence", command=self.pause_sequence, state="disabled")
        self.sequence_pause_btn.grid(row=0,column=1,padx=5,pady=8)
        self.sequence_stop_btn = ttk.Button(run, text="Stop Sequence", command=self.stop_sequence, state="disabled")
        self.sequence_stop_btn.grid(row=0,column=2,padx=5,pady=8)

        ttk.Label(run, textvariable=self.sequence_status_var, font=("Segoe UI",10,"bold")).grid(row=0,column=3,padx=(20,8))
        ttk.Label(run, textvariable=self.sequence_current_var).grid(row=0,column=4,padx=8)
        ttk.Label(run, textvariable=self.sequence_elapsed_var).grid(row=0,column=5,padx=8)
        ttk.Label(run, textvariable=self.sequence_remaining_var).grid(row=0,column=6,padx=8)

        ttk.Label(
            seq,
            text="PC-side scheduler: the laptop, application, and Ethernet connection must stay active. "
                 "Stopping the sequence leaves the chamber at the most recently transmitted setpoints.",
            foreground="#8a3b00",
            wraplength=1120
        ).pack(fill="x", padx=10, pady=(0,8))

        # ---------------- FUNCTIONS / TRACKS ----------------
        # BINDER: The interface specification exposes Track manual (0x1158)
        # as a 16-bit read/write integer. We never overwrite it blindly:
        # the user can first Read, inspect the checkboxes, then Apply.
        fbox = ttk.LabelFrame(
            self.tab_functions,
            text="MB2 Functions / Tracks — 0x1158 manual, 0x1292 read"
        )
        fbox.pack(fill="both", expand=True, padx=10, pady=10)

        ttk.Label(
            fbox,
            text=(
                "Read the current Track values first. The 16 checkboxes represent bits 0–15. "
                "Known names below follow the Functions on/off order shown in the MKF/MKFT manual. "
                "Optional functions depend on chamber configuration."
            ),
            wraplength=1120
        ).pack(fill="x", padx=10, pady=(8,4))

        values_frame = ttk.Frame(fbox)
        values_frame.pack(fill="x", padx=10, pady=5)
        ttk.Label(values_frame, text="Track manual 0x1158:", font=("Segoe UI", 9, "bold")).grid(row=0,column=0,sticky="e",padx=5,pady=3)
        ttk.Label(values_frame, textvariable=self.track_manual_value_var, font=("Consolas", 10)).grid(row=0,column=1,sticky="w",padx=5,pady=3)
        ttk.Label(values_frame, text="Track read 0x1292:", font=("Segoe UI", 9, "bold")).grid(row=1,column=0,sticky="e",padx=5,pady=3)
        ttk.Label(values_frame, textvariable=self.track_read_value_var, font=("Consolas", 10)).grid(row=1,column=1,sticky="w",padx=5,pady=3)
        ttk.Label(values_frame, textvariable=self.track_status_var).grid(row=2,column=0,columnspan=2,sticky="w",padx=5,pady=4)

        # The operating-manual screen shows these functions in this order and
        # states that controller functions count from right to left.
        known_track_names = [
            "Humidity off",
            "Idle mode",
            "Switching output 1",
            "Switching output 2",
            "Switching output 3",
            "Switching output 4",
            "Anti-condensation",
            "Compressed air valve",
            "Compressed air dryer",
        ]

        bits_frame = ttk.LabelFrame(fbox, text="Bit selection")
        bits_frame.pack(fill="x", padx=10, pady=8)

        self.track_checkbuttons = []
        for bit in range(16):
            name = known_track_names[bit] if bit < len(known_track_names) else f"Track {bit} (function name not documented here)"
            cb = ttk.Checkbutton(
                bits_frame,
                text=f"bit {bit:02d} — {name}",
                variable=self.track_bits[bit]
            )
            cb.grid(row=bit % 8, column=bit // 8, sticky="w", padx=16, pady=5)
            self.track_checkbuttons.append(cb)

        fbuttons = ttk.Frame(fbox)
        fbuttons.pack(fill="x", padx=10, pady=8)
        self.read_tracks_btn = ttk.Button(fbuttons, text="Read Functions / Tracks", command=self.read_tracks)
        self.read_tracks_btn.pack(side="left", padx=4)
        self.apply_tracks_btn = ttk.Button(fbuttons, text="Apply 0x1158 + Read-back", command=self.apply_tracks, state="disabled")
        self.apply_tracks_btn.pack(side="left", padx=4)
        ttk.Button(fbuttons, text="Clear checkboxes (local only)", command=self.clear_track_checkboxes).pack(side="left", padx=12)

        ttk.Label(
            fbox,
            text=(
                "SAFETY: Apply writes the real 16-bit Track manual register. "
                "Read-back is performed immediately after the write. "
                "Use only functions that are applicable to this chamber/options."
            ),
            foreground="#8a3b00",
            wraplength=1120
        ).pack(fill="x", padx=10, pady=(4,10))

        # ---------------- STORED PROGRAMS ----------------
        pbox = ttk.LabelFrame(
            self.tab_program,
            text="MB2 Stored Program Control — documented registers 0x1147–0x114B"
        )
        pbox.pack(fill="x", padx=10, pady=10)

        ttk.Checkbutton(
            pbox,
            text="Enable stored-program control (experimental on this chamber; commands affect the real chamber)",
            variable=self.program_control_enabled,
            command=self.refresh_write_state
        ).grid(row=0,column=0,columnspan=5,sticky="w",padx=8,pady=8)

        ttk.Label(pbox,text="Program:").grid(row=1,column=0,sticky="e",padx=7,pady=5)
        self.program_combo = ttk.Combobox(
            pbox,
            textvariable=self.program_choice_var,
            values=[self.program_display_name(i) for i in range(1,31)],
            state="readonly",
            width=40
        )
        self.program_combo.grid(row=1,column=1,columnspan=2,sticky="w",padx=7,pady=5)
        self.program_combo.bind("<<ComboboxSelected>>", self.on_program_selected)

        ttk.Label(pbox,text="Local label:").grid(row=2,column=0,sticky="e",padx=7,pady=5)
        self.program_name_entry = ttk.Entry(pbox,textvariable=self.program_name_edit_var,width=30)
        self.program_name_entry.grid(row=2,column=1,sticky="w",padx=7,pady=5)
        self.save_name_btn = ttk.Button(pbox,text="Save Local Name",command=self.save_current_program_name)
        self.save_name_btn.grid(row=2,column=2,sticky="w",padx=7,pady=5)

        ttk.Label(pbox,text="Start section register value:").grid(row=3,column=0,sticky="e",padx=7,pady=5)
        self.section_entry = ttk.Entry(pbox,textvariable=self.section_var,width=10)
        self.section_entry.grid(row=3,column=1,sticky="w",padx=7,pady=5)
        ttk.Label(
            pbox,
            text="Sent exactly as entered to 0x1148; the 2017 table does not specify zero-based vs one-based numbering.",
            foreground="#665500"
        ).grid(row=3,column=2,columnspan=3,sticky="w",padx=7,pady=5)

        self.start_btn = ttk.Button(pbox,text="START Stored Program",command=self.start_program,state="disabled")
        self.pause_btn = ttk.Button(pbox,text="PAUSE Stored Program",command=self.pause_program,state="disabled")
        self.stop_btn = ttk.Button(pbox,text="STOP Stored Program",command=self.stop_program,state="disabled")
        self.start_btn.grid(row=4,column=0,padx=7,pady=9)
        self.pause_btn.grid(row=4,column=1,padx=7,pady=9)
        self.stop_btn.grid(row=4,column=2,padx=7,pady=9)

        ttk.Label(pbox,textvariable=self.program_status_var,font=("Segoe UI",9,"bold")).grid(
            row=5,column=0,columnspan=5,sticky="w",padx=8,pady=5
        )

        ttk.Label(
            pbox,
            text=(
                "START sequence used by this software: write Program number (0x1147) -> read-back -> "
                "write Start section (0x1148) -> read-back -> write 1 to START (0x1149). "
                "If either verified setup write fails, START is NOT sent."
            ),
            wraplength=1120
        ).grid(row=6,column=0,columnspan=5,sticky="w",padx=8,pady=(5,10))

        # ---------------- DIAGNOSTICS ----------------
        diag = ttk.LabelFrame(self.tab_diag, text="Program diagnostics")
        diag.pack(fill="x", padx=10, pady=(10,6))

        ttk.Label(
            diag,
            text="Raw Modbus TX/RX diagnostics. Program commands are now located on the Stored Programs tab."
        ).pack(side="left", padx=8, pady=8)

        ttk.Button(diag, text="Clear protocol log", command=self.clear_protocol_log).pack(side="right", padx=6)
        ttk.Button(diag, text="Save protocol log", command=self.save_protocol_log).pack(side="right", padx=6)

        logf = ttk.LabelFrame(self.tab_diag, text="Raw protocol log")
        logf.pack(fill="both", expand=True, padx=10, pady=6)
        self.log_text = tk.Text(logf, height=24, wrap="none", font=("Consolas", 9), state="disabled")
        self.log_text.pack(fill="both", expand=True, padx=6, pady=6)


    def refresh_write_state(self):
        """Enable/disable real-write controls according to safety gates."""
        connected = self.sock is not None

        manual_state = "normal" if (connected and self.write_enabled.get()) else "disabled"
        for w in [self.temp_entry, self.hum_entry, self.fan_entry,
                  self.temp_btn, self.hum_btn, self.fan_btn]:
            try:
                w.config(state=manual_state)
            except Exception:
                pass

        # Functions/Tracks share the normal write-enable gate.
        try:
            self.apply_tracks_btn.config(state=manual_state)
        except Exception:
            pass

        # Stored-program control has its own second safety gate.
        program_state = "normal" if (
            connected and self.write_enabled.get() and self.program_control_enabled.get()
        ) else "disabled"

        for w in [self.start_btn, self.pause_btn, self.stop_btn]:
            try:
                w.config(state=program_state)
            except Exception:
                pass


    def require_connected_and_enabled(self):
        if not self.sock:
            messagebox.showerror("Not connected","Connect to the chamber first.")
            return False
        if not self.write_enabled.get():
            messagebox.showwarning("Write disabled","Enable write controls first.")
            return False
        return True

    def connect(self):
        if self.sock: return
        ip = self.ip_var.get().strip()
        try:
            port = int(self.port_var.get())
            interval = max(0.5,float(self.interval_var.get()))
        except ValueError:
            messagebox.showerror("Invalid settings","Check port and poll interval.")
            return
        self.status_var.set(f"Connecting to {ip}:{port} ...")
        self.connect_btn.config(state="disabled")
        def worker():
            try:
                s = socket.create_connection((ip,port),timeout=3)
                s.settimeout(2)
                self.sock = s
                self.polling = True
                self.root.after(0,self.on_connected)
                self.poll_loop(interval)
            except Exception as e:
                self.root.after(0,lambda err=str(e):self.on_connect_error(err))
        threading.Thread(target=worker,daemon=True).start()

    def on_connected(self):
        self.status_var.set("Connected")
        self.disconnect_btn.config(state="normal")
        self.refresh_write_state()

        # BINDER/SAFETY: read-only convenience. After connection we attempt to
        # display the current Track values; failure does not break live polling.
        self.root.after(300, self.read_tracks)

    def on_connect_error(self,err):
        self.sock=None
        self.polling=False
        self.connect_btn.config(state="normal")
        self.disconnect_btn.config(state="disabled")
        self.status_var.set(f"Connection failed: {err}")
        self.refresh_write_state()

    def disconnect(self, close_status=True):
        if self.sequence_running:
            self.sequence_stop_event.set()
            self.sequence_pause_event.clear()
        self.polling=False
        if self.sock:
            try:self.sock.close()
            except:pass
        self.sock=None
        self.connect_btn.config(state="normal")
        self.disconnect_btn.config(state="disabled")
        if close_status:self.status_var.set("Disconnected")
        self.program_status_var.set("Program control: IDLE")
        self.refresh_write_state()

    def poll_loop(self,interval):
        while self.polling and self.sock:
            try:
                t=read_float(self.sock,self.io_lock,REG_TEMP_ACT)
                h=read_float(self.sock,self.io_lock,REG_HUM_ACT)
                tsp=read_float(self.sock,self.io_lock,REG_TEMP_SP_READ)
                hsp=read_float(self.sock,self.io_lock,REG_HUM_SP_READ)
                try:
                    fsp=read_float(self.sock,self.io_lock,REG_FAN_SP_READ)
                except:
                    fsp=float("nan")
                now=datetime.now()
                self.root.after(0,lambda t=t,h=h,tsp=tsp,hsp=hsp,fsp=fsp,now=now:
                                self.update_values(t,h,tsp,hsp,fsp,now))
                time.sleep(interval)
            except Exception as e:
                self.root.after(0,lambda err=str(e):self.on_poll_error(err))
                break

    def on_poll_error(self,err):
        self.status_var.set(f"Communication error: {err}")
        self.disconnect(close_status=False)

    def update_values(self,t,h,tsp,hsp,fsp,now):
        self.temp_var.set(f"{t:.1f} °C")
        self.hum_var.set(f"{h:.1f} %RH")
        self.temp_sp_var.set(f"{tsp:.1f} °C")
        self.hum_sp_var.set(f"{hsp:.1f} %RH")
        self.fan_sp_var.set("--.- %" if fsp != fsp else f"{fsp:.1f} %")
        self.status_var.set(f"Connected • Last update {now.strftime('%H:%M:%S')}")
        self.history_t.append(now); self.history_temp.append(t); self.history_hum.append(h)
        if len(self.history_t)>self.max_points:
            self.history_t=self.history_t[-self.max_points:]
            self.history_temp=self.history_temp[-self.max_points:]
            self.history_hum=self.history_hum[-self.max_points:]
        if self.log_writer:
            self.log_writer.writerow([now.isoformat(timespec="seconds"),t,h,tsp,hsp,fsp])
            self.log_file.flush()
        self.draw_chart()

    # -------------------------------------------------------------------------
    # FUNCTIONS / TRACKS
    # -------------------------------------------------------------------------
    def format_track_word(self, value):
        """Return both hexadecimal and 16-bit binary representation."""
        value = int(value) & 0xFFFF
        return f"0x{value:04X} / {value:016b}"

    def set_track_checkboxes_from_word(self, value):
        """Copy bits 0..15 from an integer into the 16 GUI checkboxes."""
        value = int(value) & 0xFFFF
        for bit in range(16):
            self.track_bits[bit].set(bool(value & (1 << bit)))

    def track_word_from_checkboxes(self):
        """Build a 16-bit integer from the 16 GUI checkboxes."""
        value = 0
        for bit, var in enumerate(self.track_bits):
            if var.get():
                value |= (1 << bit)
        return value & 0xFFFF

    def clear_track_checkboxes(self):
        """Clear only the local UI. This does NOT write to the chamber."""
        for var in self.track_bits:
            var.set(False)
        self.track_status_var.set("Checkboxes cleared locally; chamber was not changed.")

    def read_tracks(self):
        """Read 0x1158 and 0x1292 without changing the chamber."""
        if not self.sock:
            messagebox.showerror("Functions / Tracks", "Connect to the chamber first.")
            return

        self.track_status_var.set("Reading Track manual 0x1158 and Track read 0x1292 ...")

        def worker():
            try:
                manual = read_word(self.sock, self.io_lock, REG_TRACK_MANUAL)
                current = read_word(self.sock, self.io_lock, REG_TRACK_READ)
                self.add_protocol_log(
                    f"TRACK READ  manual 0x1158={manual:#06x}  read 0x1292={current:#06x}"
                )
                self.root.after(
                    0,
                    lambda manual=manual, current=current: self._tracks_read_done(manual, current)
                )
            except Exception as e:
                self.root.after(
                    0,
                    lambda err=str(e): self._tracks_failed("Read Functions / Tracks", err)
                )

        threading.Thread(target=worker, daemon=True).start()

    def _tracks_read_done(self, manual, current):
        self.track_manual_value_var.set(self.format_track_word(manual))
        self.track_read_value_var.set(self.format_track_word(current))
        self.set_track_checkboxes_from_word(manual)
        self.track_status_var.set("Functions/Tracks read successfully. Checkboxes show 0x1158.")
        self.status_var.set("Functions/Tracks read: OK")

    def _tracks_failed(self, title, err):
        self.track_status_var.set(f"{title}: FAILED — {err}")
        self.status_var.set(f"{title}: FAILED")
        messagebox.showerror(title, str(err))

    def apply_tracks(self):
        """Write checkbox state to 0x1158 and verify it by reading 0x1158 back.

        SAFETY: This changes the real controller functions/tracks.
        """
        if not self.require_connected_and_enabled():
            return

        new_value = self.track_word_from_checkboxes()

        enabled_bits = [
            str(bit) for bit, var in enumerate(self.track_bits) if var.get()
        ]
        enabled_text = ", ".join(enabled_bits) if enabled_bits else "none"

        if not messagebox.askyesno(
            "Apply Functions / Tracks",
            "Write the following 16-bit value to the REAL chamber?\n\n"
            f"Register: 0x{REG_TRACK_MANUAL:04X}\n"
            f"Value: {self.format_track_word(new_value)}\n"
            f"Enabled bits: {enabled_text}\n\n"
            "The software will read 0x1158 back immediately after writing."
        ):
            return

        self.track_status_var.set("Writing Track manual 0x1158 ...")

        def worker():
            try:
                self.add_protocol_log(
                    f"TRACK WRITE  addr=0x{REG_TRACK_MANUAL:04X} value={new_value} ({new_value:#06x})"
                )
                write_single_word(self.sock, self.io_lock, REG_TRACK_MANUAL, new_value)
                time.sleep(0.25)
                readback = read_word(self.sock, self.io_lock, REG_TRACK_MANUAL)
                current = read_word(self.sock, self.io_lock, REG_TRACK_READ)

                if readback != new_value:
                    raise ValueError(
                        f"Track read-back mismatch: wrote 0x{new_value:04X}, read 0x{readback:04X}"
                    )

                self.root.after(
                    0,
                    lambda rb=readback, current=current: self._tracks_write_done(rb, current)
                )
            except Exception as e:
                self.root.after(
                    0,
                    lambda err=str(e): self._tracks_failed("Apply Functions / Tracks", err)
                )

        threading.Thread(target=worker, daemon=True).start()

    def _tracks_write_done(self, readback, current):
        self.track_manual_value_var.set(self.format_track_word(readback))
        self.track_read_value_var.set(self.format_track_word(current))
        self.set_track_checkboxes_from_word(readback)
        self.track_status_var.set("Track manual write verified by read-back.")
        self.status_var.set("Functions/Tracks: write verified")
        messagebox.showinfo(
            "Functions / Tracks",
            "Write verified.\n\n"
            f"0x1158 = {self.format_track_word(readback)}\n"
            f"0x1292 = {self.format_track_word(current)}"
        )

    def debug_write_single_word(self, address, value, timeout=1.2):
        value = int(value) & 0xFFFF
        payload = bytes([
            SLAVE_ID, 0x06,
            (address >> 8) & 0xFF, address & 0xFF,
            (value >> 8) & 0xFF, value & 0xFF
        ])
        req = frame_with_crc(payload)
        self.add_protocol_log(f"TX  addr=0x{address:04X} value={value}  {self.hexs(req)}")

        old_timeout = self.sock.gettimeout()
        with self.io_lock:
            try:
                self.sock.settimeout(timeout)
                self.sock.sendall(req)
                resp = recv_exact(self.sock, 8)
                self.add_protocol_log(f"RX  {self.hexs(resp)}")
            except socket.timeout:
                self.add_protocol_log(f"RX  TIMEOUT after {timeout:.1f}s")
                raise TimeoutError(f"No response for write to 0x{address:04X}")
            finally:
                self.sock.settimeout(old_timeout)

        verify_crc(resp)
        if resp[1] & 0x80:
            raise ValueError(f"Modbus exception code {resp[2]}")
        return resp

    def select_program_only(self):
        messagebox.showwarning("Program control disabled", "Stored-program control is available only through the separate safety gate on the Stored Programs tab.")
        return
        if not self.require_connected_and_enabled():
            return
        p = self.selected_program_number()
        p_internal = p - 1
        if not messagebox.askyesno(
            "Select Program only",
            f"Select Program {p} only?\n\nThis will write {p_internal} to 0x{REG_PROGRAM_NUMBER:04X} but will NOT start the program."
        ):
            return
        def worker():
            try:
                self.debug_write_single_word(REG_PROGRAM_NUMBER, p_internal)
                self.root.after(0, lambda: self.write_done("Select Program", "Program selection command received a response."))
            except Exception as e:
                self.root.after(0, lambda err=str(e): self.write_failed("Select Program", err))
        threading.Thread(target=worker, daemon=True).start()

    def set_section_only(self):
        messagebox.showwarning("Program control disabled", "Stored-program control is available only through the separate safety gate on the Stored Programs tab.")
        return
        if not self.require_connected_and_enabled():
            return
        try:
            sec = int(self.section_var.get())
        except Exception:
            messagebox.showerror("Start Section","Enter an integer section number.")
            return
        if sec < 1:
            messagebox.showerror("Start Section","Section must be at least 1.")
            return
        sec_internal = sec - 1
        if not messagebox.askyesno(
            "Set Start Section only",
            f"Diagnostic test only.\n\nWrite {sec_internal} to 0x{REG_START_SECTION:04X}?\nThis chamber previously timed out on this register.\nThe program will NOT be started."
        ):
            return
        def worker():
            try:
                self.debug_write_single_word(REG_START_SECTION, sec_internal)
                self.root.after(0, lambda: self.write_done("Set Start Section", "Start-section command received a response."))
            except Exception as e:
                self.root.after(0, lambda err=str(e): self.write_failed("Set Start Section", err))
        threading.Thread(target=worker, daemon=True).start()

    def start_only(self):
        messagebox.showwarning("Program control disabled", "Stored-program control is available only through the separate safety gate on the Stored Programs tab.")
        return
        if not self.require_connected_and_enabled():
            return
        if not messagebox.askyesno(
            "Send START only",
            f"Send START only?\n\nThis writes 1 to 0x{REG_START_PROGRAM:04X}.\nUse this only after Select Program and Set Start Section have both succeeded."
        ):
            return
        def worker():
            try:
                self.debug_write_single_word(REG_START_PROGRAM, 1)
                self.root.after(0, lambda: self.write_done("START only", "START command received a response."))
            except Exception as e:
                self.root.after(0, lambda err=str(e): self.write_failed("START only", err))
        threading.Thread(target=worker, daemon=True).start()

    def run_write(self, title, description, fn, verify_fn=None):
        if not self.require_connected_and_enabled(): return
        if not messagebox.askyesno(title, description + "\n\nSend this command to the real chamber?"):
            return
        self.status_var.set(f"Sending: {title} ...")
        def worker():
            try:
                fn()
                time.sleep(0.25)
                verified = verify_fn() if verify_fn else None
                msg = "Command accepted."
                if verified is not None:
                    msg += f"\nRead-back: {verified}"
                self.root.after(0,lambda m=msg:self.write_done(title,m))
            except Exception as e:
                self.root.after(0,lambda err=str(e):self.write_failed(title,err))
        threading.Thread(target=worker,daemon=True).start()

    def write_done(self,title,msg):
        self.status_var.set(f"{title}: OK")
        messagebox.showinfo(title,msg)

    def write_failed(self,title,err):
        self.status_var.set(f"{title}: FAILED — {err}")
        messagebox.showerror(title,f"Command failed:\n{err}")

    def cancel_temp_ramp(self):
        self.temp_ramp_cancel.set()
        self.status_var.set("Temperature ramp cancellation requested")

    def cancel_hum_ramp(self):
        self.hum_ramp_cancel.set()
        self.status_var.set("Humidity ramp cancellation requested")

    def run_software_ramp(self, title, start_value, target_value, minutes, interval_s, address, readback_address, unit, cancel_event):
        if minutes <= 0:
            raise ValueError("Ramp time must be greater than 0 minutes.")
        if interval_s < 1:
            raise ValueError("Ramp update interval must be at least 1 second.")

        total_s = minutes * 60.0
        steps = max(1, int(total_s / interval_s))
        cancel_event.clear()

        self.add_protocol_log(
            f"--- {title} software ramp: {start_value:.3f} -> {target_value:.3f} {unit}, "
            f"{minutes:.3f} min, {steps} steps, interval {interval_s:.1f}s ---"
        )

        for i in range(1, steps + 1):
            if cancel_event.is_set():
                self.add_protocol_log(f"{title} ramp CANCELLED at step {i}/{steps}")
                raise RuntimeError("Ramp cancelled by user")

            frac = i / steps
            value = start_value + (target_value - start_value) * frac
            write_float(self.sock, self.io_lock, address, value)
            self.add_protocol_log(f"{title} ramp step {i}/{steps}: set {value:.3f} {unit}")
            self.root.after(
                0,
                lambda i=i, steps=steps, value=value: self.status_var.set(
                    f"{title} ramp {i}/{steps} • setpoint {value:.2f} {unit}"
                )
            )

            if i < steps:
                # Sleep in short slices so Cancel reacts quickly.
                end = time.time() + interval_s
                while time.time() < end:
                    if cancel_event.is_set():
                        self.add_protocol_log(f"{title} ramp CANCELLED during wait")
                        raise RuntimeError("Ramp cancelled by user")
                    time.sleep(min(0.25, max(0.01, end - time.time())))

        rb = read_float(self.sock, self.io_lock, readback_address)
        self.add_protocol_log(f"{title} ramp COMPLETE • read-back {rb:.3f} {unit}")
        return rb

    def set_temperature(self):
        try:
            v = float(self.temp_new_var.get())
        except:
            messagebox.showerror("Temperature","Enter a numeric temperature.")
            return

        lo = -50 if self.model_var.get()=="MKF" else -80
        hi = 180
        if not (lo <= v <= hi):
            messagebox.showerror("Temperature",f"{self.model_var.get()} documented setting range: {lo} to {hi} °C.")
            return

        mode = self.temp_mode_var.get()
        if mode == "STEP":
            self.run_write(
                "Set Temperature — STEP",
                f"New temperature setpoint: {v:.1f} °C\nMode: STEP\nRegister: 0x{REG_TEMP_SP_MANUAL:04X}",
                lambda: write_float(self.sock,self.io_lock,REG_TEMP_SP_MANUAL,v),
                lambda: f"{read_float(self.sock,self.io_lock,REG_TEMP_SP_READ):.1f} °C"
            )
            return

        try:
            minutes = float(self.temp_ramp_minutes_var.get())
            interval_s = float(self.ramp_interval_var.get())
        except:
            messagebox.showerror("Temperature Ramp","Ramp time and update interval must be numeric.")
            return

        if not self.require_connected_and_enabled():
            return

        try:
            start_value = read_float(self.sock,self.io_lock,REG_TEMP_SP_READ)
        except Exception as e:
            messagebox.showerror("Temperature Ramp",f"Could not read current temperature setpoint:\n{e}")
            return

        desc = (
            f"Software RAMP temperature\n\n"
            f"Current setpoint: {start_value:.1f} °C\n"
            f"Target: {v:.1f} °C\n"
            f"Ramp time: {minutes:.2f} min\n"
            f"Update interval: {interval_s:.1f} s\n\n"
            "The laptop must remain connected and the application must stay open."
        )
        if not messagebox.askyesno("Start Temperature Ramp", desc):
            return

        def worker():
            try:
                rb = self.run_software_ramp(
                    "Temperature",
                    start_value,
                    v,
                    minutes,
                    interval_s,
                    REG_TEMP_SP_MANUAL,
                    REG_TEMP_SP_READ,
                    "°C",
                    self.temp_ramp_cancel
                )
                self.root.after(
                    0,
                    lambda rb=rb: self.write_done(
                        "Temperature Ramp",
                        f"Ramp completed.\nFinal read-back: {rb:.1f} °C"
                    )
                )
            except Exception as e:
                self.root.after(0,lambda err=str(e):self.write_failed("Temperature Ramp",err))
        threading.Thread(target=worker,daemon=True).start()

    def set_humidity(self):
        try:
            v = float(self.hum_new_var.get())
        except:
            messagebox.showerror("Humidity","Enter a numeric humidity.")
            return

        if not (0 <= v <= 100):
            messagebox.showerror("Humidity","Documented setting range: 0 to 100 %RH.")
            return

        extra = ""
        if v <= 4 or v > 99:
            extra = "\nWarning: on chambers without compressed-air dryer this range disables humidity control."

        mode = self.hum_mode_var.get()
        if mode == "STEP":
            self.run_write(
                "Set Humidity — STEP",
                f"New humidity setpoint: {v:.1f} %RH{extra}\nMode: STEP\nRegister: 0x{REG_HUM_SP_MANUAL:04X}",
                lambda: write_float(self.sock,self.io_lock,REG_HUM_SP_MANUAL,v),
                lambda: f"{read_float(self.sock,self.io_lock,REG_HUM_SP_READ):.1f} %RH"
            )
            return

        try:
            minutes = float(self.hum_ramp_minutes_var.get())
            interval_s = float(self.ramp_interval_var.get())
        except:
            messagebox.showerror("Humidity Ramp","Ramp time and update interval must be numeric.")
            return

        if not self.require_connected_and_enabled():
            return

        try:
            start_value = read_float(self.sock,self.io_lock,REG_HUM_SP_READ)
        except Exception as e:
            messagebox.showerror("Humidity Ramp",f"Could not read current humidity setpoint:\n{e}")
            return

        desc = (
            f"Software RAMP humidity\n\n"
            f"Current setpoint: {start_value:.1f} %RH\n"
            f"Target: {v:.1f} %RH\n"
            f"Ramp time: {minutes:.2f} min\n"
            f"Update interval: {interval_s:.1f} s{extra}\n\n"
            "The laptop must remain connected and the application must stay open."
        )
        if not messagebox.askyesno("Start Humidity Ramp", desc):
            return

        def worker():
            try:
                rb = self.run_software_ramp(
                    "Humidity",
                    start_value,
                    v,
                    minutes,
                    interval_s,
                    REG_HUM_SP_MANUAL,
                    REG_HUM_SP_READ,
                    "%RH",
                    self.hum_ramp_cancel
                )
                self.root.after(
                    0,
                    lambda rb=rb: self.write_done(
                        "Humidity Ramp",
                        f"Ramp completed.\nFinal read-back: {rb:.1f} %RH"
                    )
                )
            except Exception as e:
                self.root.after(0,lambda err=str(e):self.write_failed("Humidity Ramp",err))
        threading.Thread(target=worker,daemon=True).start()

    def set_fan(self):
        try:v=float(self.fan_new_var.get())
        except: messagebox.showerror("Fan","Enter a numeric fan speed."); return
        if not (30 <= v <= 100):
            messagebox.showerror("Fan","MKF 56 documented fan range: 30 to 100 %."); return
        self.run_write(
            "Set Fan",
            f"New fan setpoint: {v:.1f}%\nUse only on MKF 56.\nRegister: 0x{REG_FAN_SP_MANUAL:04X}",
            lambda: write_float(self.sock,self.io_lock,REG_FAN_SP_MANUAL,v),
            lambda: f"{read_float(self.sock,self.io_lock,REG_FAN_SP_READ):.1f} %"
        )

    # -------------------------------------------------------------------------
    # STORED PROGRAM CONTROL (MB2 0x1147..0x114B)
    # -------------------------------------------------------------------------
    def require_program_control_enabled(self):
        """Require both normal writes and the separate stored-program safety gate."""
        if not self.require_connected_and_enabled():
            return False
        if not self.program_control_enabled.get():
            messagebox.showwarning(
                "Stored Program Control",
                "Enable stored-program control on the Stored Programs tab first."
            )
            return False
        return True

    def start_program(self):
        """Select a stored program, verify setup registers, then send START.

        BINDER register table:
          0x1147 Program number: 0..24 => time programs 1..25,
                                 25..29 => week programs 26..30
          0x1148 Start section number (basis not specified in the table)
          0x1149 Start program: write 1

        SAFETY: START is never sent if 0x1147 or 0x1148 setup/verification fails.
        """
        if not self.require_program_control_enabled():
            return

        try:
            p = self.selected_program_number()
            section_raw = int(self.section_var.get())
        except Exception:
            messagebox.showerror(
                "START Stored Program",
                "Program and start-section register value must be integers."
            )
            return

        if not (1 <= p <= 30):
            messagebox.showerror("START Stored Program", "Program must be 1–30.")
            return

        if not (0 <= section_raw <= 0xFFFF):
            messagebox.showerror(
                "START Stored Program",
                "Start-section register value must be between 0 and 65535."
            )
            return

        # BINDER: This conversion IS documented:
        # register values 0..29 correspond to displayed programs 1..30.
        p_register = p - 1

        confirm_text = (
            "Send a stored-program START sequence to the REAL chamber?\n\n"
            f"Displayed program: {p}\n"
            f"0x1147 register value: {p_register}\n"
            f"0x1148 raw start-section value: {section_raw}\n\n"
            "Sequence:\n"
            "1. Write and read-back 0x1147\n"
            "2. Write and read-back 0x1148\n"
            "3. ONLY if both match, write 1 to 0x1149 START\n\n"
            "If 0x1148 times out again, START will NOT be sent."
        )

        if not messagebox.askyesno("START Stored Program", confirm_text):
            return

        self.program_status_var.set("Program control: preparing START ...")

        def worker():
            try:
                self.add_protocol_log(
                    f"--- VERIFIED PROGRAM START: display={p}, "
                    f"reg1147={p_register}, reg1148={section_raw} ---"
                )

                # Step 1: Program number.
                self.debug_write_single_word(
                    REG_PROGRAM_NUMBER, p_register, timeout=2.5
                )
                time.sleep(0.25)
                rb_program = read_word(
                    self.sock, self.io_lock, REG_PROGRAM_NUMBER
                )
                self.add_protocol_log(f"READBACK 0x1147 -> {rb_program}")

                if rb_program != p_register:
                    raise ValueError(
                        f"Program number read-back mismatch: "
                        f"wrote {p_register}, read {rb_program}"
                    )

                # Step 2: Start section.
                self.debug_write_single_word(
                    REG_START_SECTION, section_raw, timeout=2.5
                )
                time.sleep(0.25)
                rb_section = read_word(
                    self.sock, self.io_lock, REG_START_SECTION
                )
                self.add_protocol_log(f"READBACK 0x1148 -> {rb_section}")

                if rb_section != section_raw:
                    raise ValueError(
                        f"Start-section read-back mismatch: "
                        f"wrote {section_raw}, read {rb_section}"
                    )

                # Step 3: START only after both setup values were verified.
                self.debug_write_single_word(
                    REG_START_PROGRAM, 1, timeout=2.5
                )
                self.add_protocol_log(
                    "--- START 0x1149 accepted a Modbus response ---"
                )

                self.root.after(
                    0,
                    lambda: self._program_command_done(
                        "START",
                        f"Program {p}; 0x1147={rb_program}; "
                        f"0x1148={rb_section}"
                    )
                )

            except Exception as e:
                self.add_protocol_log(
                    f"--- PROGRAM START ABORTED: {e} ---"
                )
                self.root.after(
                    0,
                    lambda err=str(e): self._program_command_failed(
                        "START", err
                    )
                )

        threading.Thread(target=worker, daemon=True).start()

    def pause_program(self):
        """Write 1 to the documented MB2 PAUSE register 0x114B."""
        if not self.require_program_control_enabled():
            return
        if not messagebox.askyesno(
            "PAUSE Stored Program",
            f"Write 1 to PAUSE register 0x{REG_PAUSE_PROGRAM:04X}?"
        ):
            return

        def worker():
            try:
                self.debug_write_single_word(REG_PAUSE_PROGRAM, 1, timeout=2.5)
                self.root.after(
                    0,
                    lambda: self._program_command_done("PAUSE", "0x114B write received a response.")
                )
            except Exception as e:
                self.root.after(
                    0,
                    lambda err=str(e): self._program_command_failed("PAUSE", err)
                )

        threading.Thread(target=worker, daemon=True).start()


    def stop_program(self):
        """Write 1 to the documented MB2 STOP register 0x114A."""
        if not self.require_program_control_enabled():
            return
        if not messagebox.askyesno(
            "STOP Stored Program",
            f"Write 1 to STOP register 0x{REG_STOP_PROGRAM:04X}?"
        ):
            return

        def worker():
            try:
                self.debug_write_single_word(REG_STOP_PROGRAM, 1, timeout=2.5)
                self.root.after(
                    0,
                    lambda: self._program_command_done("STOP", "0x114A write received a response.")
                )
            except Exception as e:
                self.root.after(
                    0,
                    lambda err=str(e): self._program_command_failed("STOP", err)
                )

        threading.Thread(target=worker, daemon=True).start()

    def _program_command_done(self, command, details):
        self.program_status_var.set(f"Program control: {command} command accepted")
        self.status_var.set(f"Stored Program {command}: OK")
        messagebox.showinfo(f"Stored Program {command}", details)

    def _program_command_failed(self, command, err):
        self.program_status_var.set(
            f"Program control: {command} FAILED — {err}"
        )
        self.status_var.set(f"Stored Program {command}: FAILED")

        message = (
            f"Command failed:\n{err}\n\n"
            "For START, the application does not send 0x1149 if the "
            "verified setup steps fail."
        )
        messagebox.showerror(f"Stored Program {command}", message)


    def find_binder(self):
        self.status_var.set("Searching UDP 30718 ...")
        def worker():
            found={}
            try:
                s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM)
                s.setsockopt(socket.SOL_SOCKET,socket.SO_BROADCAST,1)
                s.settimeout(0.5); s.bind(("",0))
                for payload in (b"\x00",b""):
                    try:s.sendto(payload,("255.255.255.255",DISCOVERY_PORT))
                    except:pass
                end=time.time()+3
                while time.time()<end:
                    try:
                        data,addr=s.recvfrom(2048)
                        mac="-".join(f"{b:02X}" for b in data[-6:]) if len(data)>=6 else ""
                        found[addr[0]]=mac
                    except socket.timeout:pass
                s.close()
            except Exception as e:
                self.root.after(0,lambda err=str(e):messagebox.showerror("Find BINDER",err)); return
            self.root.after(0,lambda f=found:self.discovery_done(f))
        threading.Thread(target=worker,daemon=True).start()

    def discovery_done(self,found):
        if not found:
            self.status_var.set("No BINDER discovery response")
            messagebox.showinfo("Find BINDER","No UDP 30718 response. You can still enter the known IP manually.")
            return
        ip=list(found.keys())[0]
        self.ip_var.set(ip)
        self.status_var.set(f"Found candidate: {ip}")
        messagebox.showinfo("Find BINDER",f"Found: {ip}\nMAC: {found[ip]}\nIP field updated.")

    def toggle_logging(self):
        if self.log_writer:
            self.stop_logging(); return
        path=filedialog.asksaveasfilename(defaultextension=".csv",
            filetypes=[("CSV","*.csv")],
            initialfile=f"BINDER_log_{datetime.now().strftime('%Y%m%d_%H%M%S')}.csv")
        if not path:return
        self.log_file=open(path,"w",newline="",encoding="utf-8-sig")
        self.log_writer=csv.writer(self.log_file)
        self.log_writer.writerow(["Timestamp","Actual Temperature C","Actual Humidity %RH",
                                  "Temperature Setpoint C","Humidity Setpoint %RH","Fan Setpoint %"])
        self.log_status_var.set(f"Logging: ON • {path}")
        self.log_btn.config(text="Stop CSV Logging")

    def stop_logging(self):
        if self.log_file:
            try:self.log_file.close()
            except:pass
        self.log_file=None; self.log_writer=None
        self.log_status_var.set("Logging: OFF")
        self.log_btn.config(text="Start CSV Logging")

    def clear_chart(self):
        self.history_t.clear();self.history_temp.clear();self.history_hum.clear();self.draw_chart()

    def draw_chart(self):
        c = self.canvas
        c.delete("all")

        w = max(c.winfo_width(), 700)
        h = max(c.winfo_height(), 380)

        left = 82
        right = 82
        top = 48
        bottom = 58
        plot_w = max(120, w - left - right)
        plot_h = max(120, h - top - bottom)

        temp_color = "#1976D2"
        hum_color = "#D32F2F"
        grid_color = "#E3E7EC"
        axis_color = "#59636E"
        text_color = "#263238"

        c.configure(bg="white")

        x0, y0 = left, top
        x1, y1 = left + plot_w, top + plot_h

        # Header and legend are always visible.
        c.create_text(
            left, 16, anchor="nw",
            text="Chamber Temperature & Humidity",
            font=("Segoe UI", 13, "bold"),
            fill=text_color
        )

        legend_x = max(left + 300, x1 - 300)
        c.create_line(legend_x, 26, legend_x + 30, 26, fill=temp_color, width=4)
        c.create_text(
            legend_x + 38, 26, anchor="w",
            text="Temperature (°C)",
            font=("Segoe UI", 9, "bold"),
            fill=temp_color
        )
        c.create_line(legend_x + 160, 26, legend_x + 190, 26, fill=hum_color, width=4)
        c.create_text(
            legend_x + 198, 26, anchor="w",
            text="Humidity (%RH)",
            font=("Segoe UI", 9, "bold"),
            fill=hum_color
        )

        c.create_rectangle(x0, y0, x1, y1, fill="#FCFDFE", outline="#AEB7C0", width=1)

        n = min(len(self.history_t), len(self.history_temp), len(self.history_hum))
        temps = list(self.history_temp[-n:]) if n else []
        hums = list(self.history_hum[-n:]) if n else []
        times = list(self.history_t[-n:]) if n else []

        # Choose useful scales even before data arrives.
        if n:
            tmin, tmax = min(temps), max(temps)
            hmin, hmax = min(hums), max(hums)
        else:
            tmin, tmax = 15.0, 30.0
            hmin, hmax = 40.0, 70.0

        def padded_range(vmin, vmax, minimum_span):
            if not math.isfinite(vmin) or not math.isfinite(vmax):
                return 0.0, minimum_span
            span = vmax - vmin
            if span < minimum_span:
                mid = (vmin + vmax) / 2.0
                vmin = mid - minimum_span / 2.0
                vmax = mid + minimum_span / 2.0
                span = minimum_span
            pad = span * 0.12
            return vmin - pad, vmax + pad

        tmin, tmax = padded_range(tmin, tmax, 4.0)
        hmin, hmax = padded_range(hmin, hmax, 10.0)
        hmin = max(0.0, hmin)
        hmax = min(100.0, hmax)
        if hmax <= hmin:
            hmin, hmax = 0.0, 100.0

        # Grid + both Y axes are ALWAYS drawn.
        ticks = 5
        for i in range(ticks + 1):
            frac = i / ticks
            y = y1 - plot_h * frac
            c.create_line(x0, y, x1, y, fill=grid_color, width=1)

            tv = tmin + (tmax - tmin) * frac
            hv = hmin + (hmax - hmin) * frac

            c.create_text(
                x0 - 10, y, anchor="e",
                text=f"{tv:.1f}",
                font=("Segoe UI", 9),
                fill=temp_color
            )
            c.create_text(
                x1 + 10, y, anchor="w",
                text=f"{hv:.1f}",
                font=("Segoe UI", 9),
                fill=hum_color
            )

        # Vertical axis titles.
        c.create_text(
            18, (y0 + y1) / 2,
            text="Temperature (°C)",
            angle=90,
            font=("Segoe UI", 10, "bold"),
            fill=temp_color
        )
        c.create_text(
            w - 18, (y0 + y1) / 2,
            text="Humidity (%RH)",
            angle=270,
            font=("Segoe UI", 10, "bold"),
            fill=hum_color
        )

        # Time axis always exists.
        c.create_text(
            (x0 + x1) / 2, h - 10,
            anchor="s",
            text="Time",
            font=("Segoe UI", 10, "bold"),
            fill=axis_color
        )

        if n == 0:
            c.create_text(
                (x0 + x1) / 2, (y0 + y1) / 2,
                text="Waiting for live data...",
                font=("Segoe UI", 13, "bold"),
                fill="#7A848E"
            )
            c.create_text(
                (x0 + x1) / 2, (y0 + y1) / 2 + 28,
                text="Connect to the chamber and wait for the next poll.",
                font=("Segoe UI", 9),
                fill="#98A1AA"
            )
            return

        # Time labels for 1 or more points.
        if n == 1:
            time_positions = [(0.0, 0)]
        else:
            time_ticks = min(6, n - 1)
            time_positions = []
            for i in range(time_ticks + 1):
                frac = i / time_ticks
                idx = min(n - 1, round((n - 1) * frac))
                time_positions.append((frac, idx))

        seen = set()
        for frac, idx in time_positions:
            if idx in seen:
                continue
            seen.add(idx)
            x = x0 + plot_w * frac
            c.create_line(x, y0, x, y1, fill="#F0F2F5", width=1)
            c.create_text(
                x, y1 + 18, anchor="n",
                text=times[idx].strftime("%H:%M:%S"),
                font=("Segoe UI", 8),
                fill=axis_color
            )

        def x_for(i):
            if n <= 1:
                return x0 + plot_w / 2
            return x0 + plot_w * i / (n - 1)

        def y_temp(v):
            return y1 - plot_h * (v - tmin) / (tmax - tmin)

        def y_hum(v):
            return y1 - plot_h * (v - hmin) / (hmax - hmin)

        # One point: draw visible markers immediately.
        if n == 1:
            tx, ty = x_for(0), y_temp(temps[0])
            hx, hy = x_for(0), y_hum(hums[0])

            c.create_oval(tx - 6, ty - 6, tx + 6, ty + 6, fill=temp_color, outline="white", width=2)
            c.create_oval(hx - 6, hy - 6, hx + 6, hy + 6, fill=hum_color, outline="white", width=2)

            c.create_text(
                tx + 12, ty - 10, anchor="w",
                text=f"{temps[0]:.1f} °C",
                font=("Segoe UI", 10, "bold"),
                fill=temp_color
            )
            c.create_text(
                hx + 12, hy + 12, anchor="w",
                text=f"{hums[0]:.1f} %RH",
                font=("Segoe UI", 10, "bold"),
                fill=hum_color
            )
            return

        # Two or more points: draw lines.
        tpts = []
        hpts = []
        for i, (tv, hv) in enumerate(zip(temps, hums)):
            x = x_for(i)
            tpts.extend((x, y_temp(tv)))
            hpts.extend((x, y_hum(hv)))

        c.create_line(*tpts, fill=temp_color, width=3, smooth=True)
        c.create_line(*hpts, fill=hum_color, width=3, smooth=True)

        # Latest markers and values.
        tx, ty = x_for(n - 1), y_temp(temps[-1])
        hx, hy = x_for(n - 1), y_hum(hums[-1])

        c.create_oval(tx - 5, ty - 5, tx + 5, ty + 5, fill=temp_color, outline="white", width=2)
        c.create_oval(hx - 5, hy - 5, hx + 5, hy + 5, fill=hum_color, outline="white", width=2)

        c.create_text(
            tx - 10, max(y0 + 12, ty - 14), anchor="e",
            text=f"{temps[-1]:.1f} °C",
            font=("Segoe UI", 9, "bold"),
            fill=temp_color
        )
        c.create_text(
            hx - 10, min(y1 - 12, hy + 14), anchor="e",
            text=f"{hums[-1]:.1f} %RH",
            font=("Segoe UI", 9, "bold"),
            fill=hum_color
        )

    def on_close(self):
        self.sequence_stop_event.set(); self.sequence_pause_event.clear()
        self.temp_ramp_cancel.set(); self.hum_ramp_cancel.set()
        self.disconnect(); self.stop_logging(); self.root.destroy()

if __name__=="__main__":
    root=tk.Tk()
    BinderApp(root)
    root.mainloop()
