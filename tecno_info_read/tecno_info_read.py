# -*- coding: utf-8 -*-
"""TECNO | Infinix | itel Info Read - reads device info in Fastboot mode."""
import re
import subprocess
import threading
import tkinter as tk
from tkinter import scrolledtext

BG = "#5a5a5a"
HEADER_BG = "#161a20"
BOX_BG = "#3c3c3c"
FG = "#ffffff"


def run_fastboot(*args, serial=None):
    cmd = ["fastboot"]
    if serial:
        cmd += ["-s", serial]
    cmd += list(args)
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=15)
        return (p.stdout or "") + (p.stderr or "")
    except FileNotFoundError:
        return "ERROR: fastboot not found in PATH"
    except subprocess.TimeoutExpired:
        return ""


def parse_vars(text):
    data = {}
    for line in text.splitlines():
        line = line.replace("(bootloader)", "").strip()
        m = re.match(r"([\w\-\.]+)\s*:\s*(.*)", line)
        if m:
            data[m.group(1).lower()] = m.group(2).strip()
    return data


def first(d, *keys):
    for k in keys:
        if d.get(k):
            return d[k]
    return "Unknown"


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("TECNO | Infinix | itel Info Read")
        self.geometry("580x620")
        self.resizable(False, False)
        self.configure(bg=BG)

        header = tk.Frame(self, bg=HEADER_BG, height=90)
        header.pack(fill="x", padx=8, pady=(8, 0))
        header.pack_propagate(False)
        inner = tk.Frame(header, bg=HEADER_BG)
        inner.place(relx=0.5, rely=0.5, anchor="center")
        font = ("Arial", 26, "bold")
        for text, color in (("TECNO", "#0a84ff"), ("  |  ", "#9a9a9a"),
                            ("Infinix", "#1fbf4f"), ("  |  ", "#9a9a9a"),
                            ("itel", "#ff2a2a")):
            tk.Label(inner, text=text, fg=color, bg=HEADER_BG, font=font).pack(side="left")

        tk.Label(self, text="Connect Fastboot Mode", fg="#dddddd", bg=BG,
                 font=("Segoe UI", 14, "bold")).pack(anchor="w", padx=30, pady=(25, 5))

        row = tk.Frame(self, bg=BG)
        row.pack(fill="x", padx=30)
        self.serial = tk.Entry(row, bg=BOX_BG, fg=FG, insertbackground=FG,
                               font=("Segoe UI", 13), relief="solid", bd=1)
        self.serial.pack(side="left", fill="x", expand=True, ipady=6)
        self.btn = tk.Button(row, text="Check Device", font=("Segoe UI", 13, "bold"),
                             bg="#4a4a4a", fg=FG, activebackground="#666666",
                             activeforeground=FG, relief="raised", bd=2,
                             command=self.check)
        self.btn.pack(side="left", padx=(12, 0), ipadx=6)

        self.out = scrolledtext.ScrolledText(self, bg=BOX_BG, fg=FG, font=("Consolas", 13),
                                             relief="solid", bd=1, insertbackground=FG)
        self.out.pack(fill="both", expand=True, padx=30, pady=(20, 20))

    def log(self, text):
        self.out.delete("1.0", "end")
        self.out.insert("end", text)

    def check(self):
        self.btn.config(state="disabled")
        self.log("Waiting for device in Fastboot mode...\n")
        threading.Thread(target=self.worker, daemon=True).start()

    def worker(self):
        serial = self.serial.get().strip() or None
        if not serial:
            devs = run_fastboot("devices")
            m = re.search(r"^(\S+)\s+fastboot", devs, re.M)
            if not m:
                self.after(0, self.done, "No device found in Fastboot mode.\n" + devs)
                return
            serial = m.group(1)
            self.after(0, lambda: (self.serial.delete(0, "end"), self.serial.insert(0, serial)))

        d = parse_vars(run_fastboot("getvar", "all", serial=serial))
        d.update(parse_vars(run_fastboot("oem", "get_info", serial=serial)))

        brand = first(d, "brand", "ro.product.brand", "manufacturer").upper()
        model_no = first(d, "model", "product", "ro.product.model")
        model_name = first(d, "marketname", "market_name", "ro.product.marketname", "device_name")
        imei1 = first(d, "imei", "imei1", "imei_1")
        imei2 = first(d, "imei2", "imei_2")
        sn = first(d, "serialno", "serial", "sn") if d else serial
        if sn == "Unknown":
            sn = serial

        text = ("=== DEVICE INFORMATION ===\n\n"
                f"Brand Name: {brand}\n"
                f"Model Number: {model_no}\n"
                f"Model Name: {model_name}\n"
                f"IMEI 1: {imei1}\n"
                f"IMEI 2: {imei2}\n"
                f"Serial Number: {sn}\n")
        self.after(0, self.done, text)

    def done(self, text):
        self.log(text)
        self.btn.config(state="normal")


if __name__ == "__main__":
    App().mainloop()
