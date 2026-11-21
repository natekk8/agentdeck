"""AgentDeck desktop application for project prompts and StickS3 pairing."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import os
from pathlib import Path
from queue import Empty, SimpleQueue
import time
import tkinter as tk
from tkinter import filedialog, messagebox, simpledialog, ttk

from links import desktop_request

BG = "#090f1d"
PANEL = "#111b2e"
INPUT = "#1b2a42"
TEXT = "#f0f5fc"
MUTED = "#a9bad3"
MINT = "#73ead7"
BLUE = "#76a8ff"
HOVER = "#253f61"
ERROR = "#ff9ca5"
FONT = ("Segoe UI", 10)
SCOPE = {"Codex i Antigravity": "", "Codex": "codex", "Antigravity": "agy"}


class App:
    def __init__(self, root, data):
        self.root = root
        self.data = data
        self.inbox = SimpleQueue()
        self.pool = ThreadPoolExecutor(max_workers=2)
        self.projects = []
        self.prompts = []
        self.devices = []
        self.selected_project = ""
        self.selected_prompt = ""
        self.prompt_request = 0
        self.usb = False
        self.connected_devices = []
        self.networks = []
        self.network_device = ""
        self.scan_wait = None
        self.wifi_action = ""
        self.wifi_deadline = 0
        self.live_pending = False
        self.working = 0
        self.animation = 0
        self.last_device_signature = None
        root.title("AgentDeck | Windows")
        root.geometry("995x670")
        root.minsize(760, 575)
        root.configure(bg=BG)
        root.option_add("*Font", FONT)
        self.styles()
        self.build()
        self.root.after(60, self.drain)
        self.root.after(30, self.animate)
        self.task("projects", self.receive_projects)
        self.refresh_links()

    def styles(self):
        style = ttk.Style(self.root)
        style.theme_use("clam")
        style.configure("TNotebook", background=BG, borderwidth=0, tabmargins=(0, 0, 0, 0),
                        bordercolor=PANEL, lightcolor=PANEL, darkcolor=PANEL)
        style.configure("TNotebook.Tab", background=PANEL, foreground=MUTED, padding=(18, 10),
                        bordercolor=INPUT, lightcolor=INPUT, darkcolor=INPUT, borderwidth=0)
        style.map("TNotebook.Tab", background=[("selected", INPUT)], foreground=[("selected", MINT)])
        style.configure("TCombobox", padding=5, foreground=TEXT, fieldbackground=INPUT,
                        background=INPUT, selectforeground=TEXT, selectbackground=INPUT,
                        bordercolor=HOVER, lightcolor=INPUT, darkcolor=INPUT, arrowcolor=MUTED)
        style.map("TCombobox", fieldbackground=[("readonly", INPUT)], foreground=[("readonly", TEXT)],
                  bordercolor=[("focus", BLUE), ("readonly", HOVER)])

    def frame(self, parent, bg=BG):
        return tk.Frame(parent, bg=bg)

    def label(self, parent, text="", color=TEXT, size=10, bg=BG, bold=False):
        return tk.Label(parent, text=text, fg=color, bg=bg, anchor="w",
                        font=("Segoe UI", size, "bold" if bold else "normal"))

    def button(self, parent, text, command, main=False):
        color = MINT if main else INPUT
        widget = tk.Button(parent, text=text, command=command, bd=0, padx=12, pady=8,
                           fg=BG if main else TEXT, bg=color, activeforeground=BG,
                           activebackground=MINT, highlightthickness=0, cursor="hand2")
        widget.bind("<Enter>", lambda _: widget.configure(bg="#a3f5e8" if main else HOVER))
        widget.bind("<Leave>", lambda _: widget.configure(bg=color))
        return widget

    def entry(self, parent, variable):
        return tk.Entry(parent, textvariable=variable, bg=INPUT, fg=TEXT,
                        insertbackground=TEXT, relief="flat", highlightthickness=5,
                        highlightbackground=INPUT, highlightcolor=HOVER,
                        disabledbackground=BG, disabledforeground=MUTED)

    def listbox(self, parent):
        return tk.Listbox(parent, height=6, bg=INPUT, fg=TEXT, selectbackground="#2b4d60",
                          selectforeground=TEXT, borderwidth=0, highlightthickness=0,
                          activestyle="none", exportselection=False)

    def build(self):
        top = self.frame(self.root)
        top.pack(fill="x", padx=26, pady=(19, 13))
        brand = tk.Canvas(top, width=36, height=36, bg=BG, highlightthickness=0)
        brand.pack(side="left", padx=(0, 10))
        brand.create_rectangle(2, 2, 34, 34, fill=INPUT, outline=INPUT)
        brand.create_line(11, 12, 17, 18, 11, 24, fill=MINT, width=2)
        brand.create_line(20, 24, 27, 24, fill=BLUE, width=2)
        self.label(top, "AgentDeck", size=25, bold=True).pack(side="left")
        connection = self.frame(top, PANEL)
        connection.pack(side="right", pady=4)
        self.link_dot = tk.Canvas(connection, width=17, height=20, bg=PANEL, highlightthickness=0)
        self.link_dot.pack(side="left", padx=(8, 0))
        self.link_dot.create_oval(5, 7, 11, 13, fill=BLUE, outline="", tags="dot")
        self.link_label = self.label(connection, "Mostek", MUTED, bg=PANEL)
        self.link_label.pack(side="left", padx=(2, 11), pady=5)
        self.activity = tk.Canvas(self.root, height=3, bg=INPUT, highlightthickness=0)
        self.activity.pack(fill="x", padx=26, pady=(0, 12))
        self.activity.create_rectangle(0, 0, 0, 3, fill=BLUE, width=0, tags="signal")
        self.tabs = ttk.Notebook(self.root)
        self.tabs.pack(fill="both", expand=True, padx=25)
        self.prompt_tab = self.frame(self.tabs, PANEL)
        self.device_tab = self.frame(self.tabs, PANEL)
        self.wifi_tab = self.frame(self.tabs, PANEL)
        self.tabs.add(self.prompt_tab, text="Prompty")
        self.tabs.add(self.device_tab, text="Urządzenia")
        self.tabs.add(self.wifi_tab, text="Wi-Fi")
        self.tabs.bind("<<NotebookTabChanged>>", self.tab_changed)
        self.build_prompts()
        self.build_devices()
        self.build_wifi()
        self.status = self.label(self.root, "Łączenie z mostkiem…", MUTED)
        self.status.pack(side="bottom", before=self.tabs, fill="x", padx=26, pady=(7, 12))
        self.root.bind("<Control-s>", lambda _: self.save_prompt() if self.tabs.index("current") == 0 else None)

    def build_prompts(self):
        top = self.frame(self.prompt_tab, PANEL)
        top.pack(fill="x", padx=14, pady=14)
        self.label(top, "Projekt", bg=PANEL, bold=True).pack(side="left", padx=(0, 10))
        self.project_picker = ttk.Combobox(top, state="readonly", height=12)
        self.project_picker.pack(side="left", fill="x", expand=True, padx=(0, 8))
        self.project_picker.bind("<<ComboboxSelected>>", lambda _: self.project_changed())
        self.button(top, "Dodaj folder…", self.add_project).pack(side="left", padx=(0, 5))
        self.button(top, "Identyfikator Antigravity", self.bind_project).pack(side="left")
        body = self.frame(self.prompt_tab, PANEL)
        body.pack(fill="both", expand=True, padx=14, pady=(0, 14))
        body.columnconfigure(0, weight=1)
        body.columnconfigure(1, weight=3)
        body.rowconfigure(0, weight=1)
        left = self.frame(body, PANEL)
        left.grid(row=0, column=0, sticky="nsew", padx=(0, 14))
        self.button(left, "+ Nowy prompt", self.clear_prompt).pack(fill="x", pady=(0, 8))
        self.prompt_list = self.listbox(left)
        self.prompt_list.pack(fill="both", expand=True)
        self.prompt_list.bind("<<ListboxSelect>>", self.prompt_chosen)

        editor = self.frame(body, PANEL)
        editor.grid(row=0, column=1, sticky="nsew")
        self.label(editor, "Nazwa", bg=PANEL, bold=True).pack(fill="x", pady=(0, 3))
        self.title = tk.StringVar()
        self.entry(editor, self.title).pack(fill="x", pady=(0, 9))
        self.label(editor, "Dostawca", bg=PANEL, bold=True).pack(fill="x", pady=(0, 3))
        self.provider = ttk.Combobox(editor, state="readonly", values=list(SCOPE))
        self.provider.current(0)
        self.provider.pack(fill="x", pady=(0, 9))
        self.label(editor, "Treść promptu", bg=PANEL, bold=True).pack(fill="x", pady=(0, 3))
        self.prompt_body = tk.Text(editor, height=7, wrap="word", undo=True, relief="flat",
                                   highlightthickness=0, bg=INPUT, fg=TEXT, insertbackground=TEXT,
                                   padx=8, pady=8, font=FONT)
        self.prompt_body.pack(fill="both", expand=True)
        actions = self.frame(editor, PANEL)
        actions.pack(fill="x", pady=(9, 0))
        self.button(actions, "Zapisz prompt", self.save_prompt, main=True).pack(side="right")
        self.button(actions, "Usuń", self.delete_prompt).pack(side="right", padx=(0, 7))
        self.button(actions, "Pokaż projekt na Sticku", self.show_project).pack(side="left")

    def build_devices(self):
        surface = self.frame(self.device_tab, PANEL)
        surface.pack(fill="both", expand=True, padx=17, pady=17)
        self.label(surface, "Sparuj M5Stick S3", size=16, bg=PANEL, bold=True).pack(fill="x")
        self.label(surface, "Kod znajdziesz na Sticku w Komputery → Sparuj komputer.",
                   MUTED, bg=PANEL).pack(fill="x", pady=(5, 11))
        toolbar = self.frame(surface, PANEL)
        toolbar.pack(fill="x")
        self.button(toolbar, "USB bez kodu", self.pair_usb, main=True).pack(side="left", padx=(0, 8))
        self.button(toolbar, "Pokaż kod na Sticku", self.show_pairing).pack(side="left", padx=(0, 8))
        self.button(toolbar, "Szukaj w sieci", self.discover).pack(side="left")
        self.found = ttk.Combobox(surface, state="readonly", height=6)
        self.found.pack(fill="x", pady=(12, 8))
        values = self.frame(surface, PANEL)
        values.pack(fill="x")
        self.code = tk.StringVar()
        self.address = tk.StringVar()
        self.label(values, "Kod ze Sticka", bg=PANEL, bold=True).grid(row=0, column=0, sticky="w")
        self.label(values, "Adres IP Sticka (opcjonalny)", bg=PANEL).grid(row=0, column=1, sticky="w", padx=(8, 0))
        self.entry(values, self.code).grid(row=1, column=0, sticky="ew", pady=(3, 0))
        self.entry(values, self.address).grid(row=1, column=1, sticky="ew", padx=8, pady=(3, 0))
        values.columnconfigure(0, weight=1)
        values.columnconfigure(1, weight=1)
        self.button(values, "Sparuj przez Wi-Fi", self.pair_lan, main=True).grid(row=1, column=2, sticky="ew")
        self.button(values, "USB z kodem", lambda: self.pair_usb(use_code=True)).grid(row=1, column=3, padx=(7, 0))
        context = self.frame(surface, INPUT)
        context.pack(fill="x", pady=(14, 2))
        self.context_label = self.label(context, "Widok Sticka pojawi się po połączeniu.", MUTED, bg=INPUT)
        self.context_label.pack(side="left", fill="x", expand=True, padx=8)
        self.button(context, "Otwórz projekt ze Sticka", self.use_stick_project).pack(side="right", padx=3)
        bar = self.frame(surface, PANEL)
        bar.pack(fill="x", pady=(22, 6))
        self.label(bar, "Sparowane urządzenia", bg=PANEL, bold=True).pack(side="left")
        self.button(bar, "Odśwież", lambda: self.task("devices", self.receive_links)).pack(side="right")
        self.paired_list = self.listbox(surface)
        self.paired_list.pack(fill="both", expand=True)
        self.button(surface, "Usuń wybrane parowanie", self.forget).pack(anchor="e", pady=(8, 0))
        self.paired = []

    def build_wifi(self):
        surface = self.frame(self.wifi_tab, PANEL)
        surface.pack(fill="both", expand=True, padx=17, pady=17)
        self.label(surface, "Wi-Fi Sticka", size=17, bg=PANEL, bold=True).pack(fill="x")
        bar = self.frame(surface, PANEL)
        bar.pack(fill="x", pady=(10, 7))
        self.wifi_picker = ttk.Combobox(bar, state="readonly", height=6)
        self.wifi_picker.pack(side="left", fill="x", expand=True)
        self.wifi_picker.bind("<<ComboboxSelected>>", self.wifi_device_changed)
        self.button(bar, "Skanuj przez Sticka", self.scan_wifi, main=True).pack(side="right", padx=(8, 0))
        self.wifi_state = self.label(surface, "Podłącz Stick przez USB lub wybierz go w sieci.",
                                     MUTED, bg=PANEL)
        self.wifi_state.pack(fill="x", pady=(3, 10))
        self.wifi_list = self.listbox(surface)
        self.wifi_list.pack(fill="both", expand=True)
        self.wifi_list.bind("<<ListboxSelect>>", self.network_chosen)
        form = self.frame(surface, PANEL)
        form.pack(fill="x", pady=(11, 0))
        self.network_name = self.label(form, "Wybierz sieć z listy", bg=PANEL, bold=True)
        self.network_name.grid(row=0, column=0, sticky="w", pady=(0, 5))
        self.show_password = tk.BooleanVar(value=False)
        self.network_password = tk.StringVar()
        self.password_input = self.entry(form, self.network_password)
        self.password_input.configure(show="•", state="disabled")
        self.password_input.grid(row=1, column=0, sticky="ew", padx=(0, 8))
        tk.Checkbutton(form, text="Pokaż hasło", variable=self.show_password, bg=PANEL, fg=MUTED,
                       selectcolor=INPUT, activebackground=PANEL, activeforeground=TEXT,
                       command=self.password_visibility).grid(row=1, column=1, padx=(0, 8))
        self.button(form, "Połącz Sticka", self.connect_wifi, main=True).grid(row=1, column=2)
        form.columnconfigure(0, weight=1)
        self.label(surface, "Hasło jest przekazywane do Sticka i zapisywane po udanym połączeniu.",
                   MUTED, bg=PANEL).pack(fill="x", pady=(9, 0))

    def animate(self):
        running = self.working or self.scan_wait or self.wifi_action
        if running:
            self.animation = (self.animation + 13) % max(90, self.activity.winfo_width() + 100)
            self.activity.coords("signal", self.animation - 100, 0, self.animation, 3)
        elif self.animation:
            self.activity.coords("signal", 0, 0, 0, 3)
            self.animation = 0
        self.root.after(42 if running else 170, self.animate)

    def note(self, message, error=False):
        self.status.configure(text=message, fg=ERROR if error else MUTED)

    def task(self, operation, accept=None, report=None, **fields):
        if report:
            self.note(report)
            self.working += 1

        def run():
            try:
                self.inbox.put((operation, accept, desktop_request(self.data, operation, **fields), report))
            except (OSError, ValueError, KeyError) as error:
                self.inbox.put((operation, accept, {"error": str(error)}, report))

        self.pool.submit(run)

    def drain(self):
        while True:
            try:
                operation, accept, result, report = self.inbox.get_nowait()
            except Empty:
                break
            if report:
                self.working = max(0, self.working - 1)
            if "error" in result:
                if operation == "devices":
                    self.live_pending = False
                if report or operation != "devices":
                    self.note(result["error"], error=True)
            elif accept:
                accept(result)
        self.root.after(60, self.drain)

    def receive_projects(self, result, select=None):
        self.projects = result["items"]
        self.project_picker.configure(values=[f'{p["label"]}   ·   {p["path"]}' for p in self.projects])
        search = select or self.selected_project
        for index, project in enumerate(self.projects):
            if project["id"] == search:
                self.project_picker.current(index)
                break
        else:
            if self.projects:
                self.project_picker.current(0)
        self.project_changed()

    def project_changed(self):
        position = self.project_picker.current()
        if position < 0:
            self.note("Dodaj folder projektu.")
            return
        self.selected_project = self.projects[position]["id"]
        self.clear_prompt()
        self.reload_prompts()

    def reload_prompts(self, select=""):
        self.prompt_request += 1
        generation = self.prompt_request

        def show(result):
            if generation != self.prompt_request:
                return
            self.prompts = sorted(result["items"], key=lambda item: item["label"].casefold())
            self.prompt_list.delete(0, "end")
            for index, item in enumerate(self.prompts):
                self.prompt_list.insert("end", ("  " + item["label"]))
                if item["id"] == select:
                    self.prompt_list.selection_set(index)
                    self.prompt_list.see(index)
                    self.fill_prompt(item)
            self.note("Wybierz lub dodaj prompt." if self.prompts else "Ten projekt jest pusty. Utwórz pierwszy prompt.")

        self.task("prompts", show, project=self.selected_project)

    def clear_prompt(self):
        self.selected_prompt = ""
        self.prompt_list.selection_clear(0, "end")
        self.title.set("")
        self.provider.current(0)
        self.prompt_body.delete("1.0", "end")

    def fill_prompt(self, item):
        self.selected_prompt = item["id"]
        self.title.set(item["label"])
        self.provider.current(list(SCOPE.values()).index(item["provider"]))
        self.prompt_body.delete("1.0", "end")
        self.prompt_body.insert("1.0", item["text"])

    def prompt_chosen(self, _):
        selected = self.prompt_list.curselection()
        if selected and selected[0] < len(self.prompts):
            self.fill_prompt(self.prompts[selected[0]])

    def save_prompt(self):
        if not self.selected_project:
            self.note("Wybierz projekt przed zapisaniem.", error=True)
            return
        active = self.selected_project

        def done(result):
            if active == self.selected_project:
                self.reload_prompts(result["item"]["id"])

        self.task("save_prompt", done, "Zapisuję prompt…", project=active,
                  id=self.selected_prompt, label=self.title.get(),
                  provider=SCOPE[self.provider.get()], text=self.prompt_body.get("1.0", "end-1c"))

    def delete_prompt(self):
        if self.selected_prompt and messagebox.askyesno("Usuń prompt", "Usunąć ten prompt z projektu?"):
            active = self.selected_project

            def done(_):
                if active == self.selected_project:
                    self.clear_prompt()
                    self.reload_prompts()

            self.task("delete_prompt", done, project=active, id=self.selected_prompt)

    def add_project(self):
        folder = filedialog.askdirectory(title="Wybierz folder projektu")
        if folder:
            self.store_project(folder, "")

    def bind_project(self):
        index = self.project_picker.current()
        if index < 0:
            return
        item = self.projects[index]
        native = simpledialog.askstring("Projekt Antigravity", "Identyfikator lub dokładna nazwa projektu",
                                        initialvalue=item["agy_project"], parent=self.root)
        if native is not None:
            self.store_project(item["path"], native)

    def store_project(self, folder, native):
        def done(result):
            self.task("projects", lambda entries: self.receive_projects(entries, result["id"]))

        self.task("add_project", done, "Zapisuję folder…", path=folder, agy_project=native)

    def tab_changed(self, _):
        if self.tabs.index("current") in (1, 2) and not self.live_pending:
            self.live_pending = True
            self.task("devices", self.receive_links)

    def refresh_links(self):
        if not self.live_pending:
            self.live_pending = True
            self.task("devices", self.receive_links)
        self.root.after(900, self.refresh_links)

    def current_device(self):
        return next((d for d in self.connected_devices if d["id"] == self.network_device), {})

    def receive_links(self, result):
        self.live_pending = False
        self.usb = result["usb"]
        self.paired = result["paired"]
        self.connected_devices = result.get("devices", [])
        active = self.usb or any(p["online"] for p in self.paired)
        self.link_dot.itemconfigure("dot", fill=MINT if active else BLUE)
        self.link_label.configure(text="StickS3 przez USB" if self.usb else
                                  "StickS3 przez Wi-Fi" if any(p["online"] for p in self.paired)
                                  else "Mostek gotowy")
        signature = [(d["id"], d.get("usb"), d["online"], d["address"], d.get("paired"))
                     for d in self.connected_devices]
        if signature != self.last_device_signature:
            self.last_device_signature = signature
            old_selection = self.paired_list.curselection()
            self.paired_list.delete(0, "end")
            for p in self.paired:
                status = "Wi-Fi aktywne" if p["online"] else "Sparowano"
                self.paired_list.insert("end", f'  StickS3 {p["id"][-4:].upper()}   ·   {p["address"] or "adres oczekuje"}   ·   {status}')
            if old_selection and old_selection[0] < len(self.paired):
                self.paired_list.selection_set(old_selection[0])
            self.wifi_picker.configure(values=[
                f'StickS3 {d["id"][-4:].upper()}  ·  ' + ("USB" if d["usb"] else
                                                         "Wi-Fi" if d["online"] else "Offline")
                for d in self.connected_devices
            ])
            ids = [d["id"] for d in self.connected_devices]
            if self.network_device not in ids:
                self.network_device = next((d["id"] for d in self.connected_devices if d["usb"]),
                                           ids[0] if ids else "")
                self.reset_wifi_controls()
            if self.network_device:
                self.wifi_picker.current(ids.index(self.network_device))
            else:
                self.wifi_picker.set("")
        snapshot = self.current_device().get("snapshot", {})
        context = snapshot.get("project_name") or snapshot.get("session") or snapshot.get("screen")
        self.context_label.configure(text=f"Na Sticku   {context}" if context else "Widok Sticka pojawi się po połączeniu.")
        self.update_wifi_snapshot(snapshot)

    def wifi_device_changed(self, _):
        index = self.wifi_picker.current()
        if index >= 0 and self.network_device != self.connected_devices[index]["id"]:
            self.network_device = self.connected_devices[index]["id"]
            self.reset_wifi_controls()
        self.update_wifi_snapshot(self.current_device().get("snapshot", {}))

    def reset_wifi_controls(self):
        self.networks.clear()
        self.wifi_list.delete(0, "end")
        self.network_chosen(None)
        self.scan_wait = None
        self.wifi_action = ""

    def update_wifi_snapshot(self, snapshot):
        device = self.current_device()
        if not device or not (device.get("usb") or device.get("online")):
            self.wifi_state.configure(text="Stick jest offline. Podłącz przewód albo sprawdź sieć.")
        else:
            ssid, address = snapshot.get("ssid"), snapshot.get("address")
            label = f'{ssid}  ·  {address}' if ssid else "Stick bez połączenia Wi-Fi"
            message = snapshot.get("wifi_message")
            if not ssid or snapshot.get("wifi_phase") in {"connecting", "failed", "unsaved"}:
                if message:
                    label += "  ·  " + message
            self.wifi_state.configure(text=label)

        if self.scan_wait:
            selected, revision, deadline = self.scan_wait
            if selected == self.network_device and snapshot.get("scan_revision", 0) >= revision:
                self.scan_wait = None
                self.task("wifi_list", lambda r: self.receive_networks(r, selected),
                          "Odczytuję sieci ze Sticka…", device=selected)
            elif time.monotonic() >= deadline:
                self.scan_wait = None
                self.note("Stick nie odesłał skanu. Spróbuj ponownie.", error=True)
        if self.wifi_action:
            phase = snapshot.get("wifi_phase")
            if snapshot.get("wifi_action") == self.wifi_action and phase in {"connected", "failed", "unsaved"}:
                self.wifi_action = ""
                self.note("Stick połączył się z siecią." if phase == "connected"
                          else snapshot.get("wifi_message") or "Sprawdź połączenie Sticka.",
                          error=phase != "connected")
            elif time.monotonic() >= self.wifi_deadline:
                self.wifi_action = ""
                self.note("Po zmianie sieci sprawdź połączenie na Sticku. USB pozwoli odczytać wynik.", error=True)

    def scan_wifi(self):
        device = self.network_device
        if not device:
            self.note("Połącz Stick, aby skanować Wi-Fi.", error=True)
            return

        def scanning(result):
            if device == self.network_device:
                self.scan_wait = (device, result["target_revision"], time.monotonic() + 14)
                self.note("Stick skanuje Wi-Fi…")

        self.task("wifi_scan", scanning, "Uruchamiam skan Sticka…", device=device)

    def receive_networks(self, result, device):
        if device != self.network_device:
            return
        self.networks = result["networks"]
        self.wifi_list.delete(0, "end")
        for item in self.networks:
            kind = "Hasło" if item["secured"] else "Otwarta"
            self.wifi_list.insert("end", f'  {item["ssid"]}  ·  {kind}  ·  {item["rssi"]} dBm')
        self.network_chosen(None)
        self.note(f'Stick znalazł {len(self.networks)} sieci.' if self.networks else "Stick nie znalazł dostępnych sieci.")

    def network_chosen(self, _):
        selected = self.wifi_list.curselection()
        item = self.networks[selected[0]] if selected else None
        self.network_name.configure(text=item["ssid"] if item else "Wybierz sieć z listy")
        self.network_password.set("")
        self.show_password.set(False)
        self.password_visibility()
        self.password_input.configure(state="normal" if item and item["secured"] else "disabled")

    def password_visibility(self):
        self.password_input.configure(show="" if self.show_password.get() else "•")

    def connect_wifi(self):
        selected = self.wifi_list.curselection()
        if not selected:
            self.note("Wybierz sieć z listy.", error=True)
            return
        network = self.networks[selected[0]]
        secret = self.network_password.get() if network["secured"] else ""
        if network["secured"] and not 8 <= len(secret.encode()) <= 63:
            self.note("Wpisz hasło od 8 do 63 bajtów.", error=True)
            return
        device = self.network_device

        def awaiting(result):
            if device == self.network_device:
                self.wifi_action = result["wifi_action"]
                self.wifi_deadline = time.monotonic() + 28
                self.note("Stick łączy się z wybraną siecią…")

        self.task("wifi_connect", awaiting, "Przekazuję ustawienia do Sticka…",
                  device=device, ssid=network["ssid"], password=secret)
        self.network_password.set("")
        self.show_password.set(False)
        self.password_visibility()

    def show_project(self):
        if not self.selected_project:
            self.note("Wybierz projekt.", error=True)
            return
        provider = SCOPE[self.provider.get()] or self.current_device().get("snapshot", {}).get("provider") or "codex"
        self.task("show_project", lambda _: self.note("Projekt jest otwarty na Sticku."),
                  "Przekazuję projekt…", device=self.network_device, project=self.selected_project, provider=provider)

    def use_stick_project(self):
        project = self.current_device().get("snapshot", {}).get("project")
        choice = next((i for i, entry in enumerate(self.projects) if entry["id"] == project), None)
        if choice is None:
            self.note("Otwórz projekt na Sticku, aby przejść do niego w Windows.")
            return
        saved = next((p for p in self.prompts if p["id"] == self.selected_prompt), {})
        changed = (self.title.get() != saved.get("label", "") or
                   self.prompt_body.get("1.0", "end-1c") != saved.get("text", "") or
                   SCOPE[self.provider.get()] != saved.get("provider", ""))
        if self.project_picker.current() != choice:
            if changed and not messagebox.askyesno("Zmień projekt", "Przejść do projektu ze Sticka bez zapisywania promptu?"):
                return
            self.project_picker.current(choice)
            self.project_changed()
        self.tabs.select(self.prompt_tab)

    def discover(self):
        def display(result):
            self.devices = result["devices"]
            self.found.configure(values=[f'StickS3 {item["id"][-4:].upper()}   ·   {item["address"]}'
                                         + ("   ·   kod aktywny" if item["pairing"] else "") for item in self.devices])
            self.found.set("")
            if self.devices:
                position = next((i for i, item in enumerate(self.devices) if item["pairing"]), 0)
                self.found.current(position)
            self.note("Wybierz urządzenie i wpisz kod ze Sticka." if self.devices else "Brak Sticków w tej sieci.")

        self.task("discover", display, "Szukam urządzeń…", address=self.address.get().strip())

    def pair_done(self, _):
        self.code.set("")
        self.note("StickS3 został sparowany.")
        self.root.after(700, lambda: self.task("devices", self.receive_links))

    def pair_usb(self, use_code=False):
        code = self.code.get().strip() if use_code else ""
        if use_code and not code:
            self.note("Wpisz kod wyświetlony na Sticku.", error=True)
            return
        self.task("pair_usb", self.pair_done, "Parowanie przez USB…", code=code)

    def show_pairing(self):
        self.task("show_pairing", lambda _: self.note("Wpisz kod widoczny na ekranie Sticka."),
                  "Otwieram kod na Sticku…")

    def pair_lan(self):
        selected = self.found.current()
        address = self.address.get().strip() or (self.devices[selected]["address"] if selected >= 0 else "")
        self.task("pair_lan", self.pair_done, "Sprawdzam kod…", address=address, code=self.code.get())

    def forget(self):
        selected = self.paired_list.curselection()
        if selected:
            self.task("forget", lambda _: self.task("devices", self.receive_links),
                      device=self.paired[selected[0]]["id"])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", type=Path, default=Path("D:/AgentDeck-data") if Path("D:/").exists()
                        else Path(os.environ["LOCALAPPDATA"]) / "AgentDeck")
    args = parser.parse_args()
    root = tk.Tk()
    app = App(root, args.data)
    try:
        root.mainloop()
    finally:
        app.pool.shutdown(wait=False, cancel_futures=True)


if __name__ == "__main__":
    main()
