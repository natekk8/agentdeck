import json
import os
from pathlib import Path
import secrets
import tempfile
import time
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from agentdeck import ACTIVE, Deck, ROOT
from links import Links, desktop_request, desktop_server
import providers


class BridgeTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.args = SimpleNamespace(data=Path(self.temp.name), roots=[], project=[ROOT],
                                    demo=True, allow_shutdown=False)
        self.deck = Deck(self.args)
        self.addCleanup(self.deck.catalog_pool.shutdown)
        self.wait(lambda: not self.deck.pending)
        self.project = next(iter(self.deck.projects))
        self.assertEqual([], self.send("prompts", project=self.project, provider="codex")["items"])
        self.prompt = self.deck.desktop("save_prompt", {
            "project": self.project, "label": "Test", "text": "Local test", "provider": "",
        })["item"]["id"]

    def wait(self, predicate, seconds=8):
        until = time.monotonic() + seconds
        while time.monotonic() < until:
            if predicate():
                return
            time.sleep(.025)
        self.fail("Timed out waiting for test state")

    def send(self, op, **values):
        return self.deck.request({"id": secrets.token_hex(12), "op": op, **values})

    def launch(self, provider="codex"):
        result = self.send("start", provider=provider, project=self.project, model="demo", prompt=self.prompt)
        self.assertTrue(result["ok"], result)
        return result["job"]["id"]

    def test_two_sessions_and_individual_cancel(self):
        a = self.launch()
        b = self.launch("agy")
        c = self.launch()
        self.deck.tick()
        self.assertEqual(self.deck.jobs[a]["state"], "running")
        self.assertEqual(self.deck.jobs[b]["state"], "running")
        self.assertEqual(self.deck.jobs[c]["state"], "queued")
        self.assertTrue(self.send("stop", job=a)["ok"])
        self.wait(lambda: self.deck.jobs[a]["state"] == "cancelled")
        self.assertEqual(self.deck.jobs[b]["state"], "running")
        self.assertTrue(self.send("stop", job=b)["ok"])
        self.assertTrue(self.send("stop", job=c)["ok"])
        self.wait(lambda: not any(j["state"] in ACTIVE for j in self.deck.jobs.values()))
        repeat = self.send("retry", job=a)
        self.assertTrue(repeat["ok"])
        self.assertNotEqual(repeat["job"]["id"], a)
        self.assertEqual(repeat["job"]["state"], "queued")

    def test_idempotency_and_restart(self):
        request = {"id": secrets.token_hex(12), "op": "start", "provider": "codex",
                   "project": self.project, "model": "demo", "prompt": self.prompt}
        a = self.deck.request(request)
        self.assertEqual(a, self.deck.request(request))
        self.assertEqual(1, len(self.deck.jobs))
        restarted = Deck(self.args)
        self.addCleanup(restarted.catalog_pool.shutdown)
        self.assertEqual("orphaned", restarted.jobs[request["id"]]["state"])
        result = restarted.request({"id": secrets.token_hex(12), "op": "power", "armed": True, "confirm": "manual"})
        self.assertFalse(result["ok"])

    def test_shutdown_requires_manual_confirmation_and_new_work_cancels(self):
        self.assertFalse(self.send("power", armed=True)["ok"])
        with patch("agentdeck.time.monotonic", return_value=1000):
            self.assertTrue(self.send("power", armed=True, confirm="manual")["ok"])
            self.assertEqual(self.send("status")["cooldown"], 30)
        with patch("agentdeck.time.monotonic", return_value=1029):
            x = self.launch()
            self.assertIsNone(self.deck.deadline)
            self.assertFalse(self.send("power", armed=True, confirm="manual")["ok"])
            self.send("stop", job=x)
            self.deck.tick()
            self.assertIsNone(self.deck.deadline)
            self.assertTrue(self.send("power", armed=True, confirm="manual")["ok"])
            self.assertTrue(self.send("power", armed=False)["ok"])
        with patch("agentdeck.time.monotonic", return_value=1060):
            self.deck.tick()
            self.assertFalse(self.send("status")["power_sent"])
            self.send("power", armed=True, confirm="manual")
            self.deck.tick()
        with patch("agentdeck.time.monotonic", return_value=1091):
            self.deck.tick()
            self.assertTrue(self.send("status")["power_sent"])

    def test_stick_shows_only_five_most_recent_sessions(self):
        created = [self.launch() for _ in range(7)]
        self.deck.jobs[created[-2]]["source"] = "desktop"
        self.deck.jobs[created[-3]]["state"] = "finished"
        result = self.send("sessions")
        self.assertEqual([created[i] for i in (6, 4, 3, 2, 1)], [row["id"] for row in result["items"]])
        self.deck.tick()
        self.assertIsNone(self.deck.deadline)
        for ident in created:
            self.send("stop", job=ident)
        self.wait(lambda: not any(job["state"] in ACTIVE for job in self.deck.jobs.values()))

    def test_model_picker_uses_one_available_id_per_name(self):
        models = [
            {"id": "neon-xhigh", "label": "Neon (XHigh)"},
            {"id": "neon-low", "label": "Neon (Low)"},
            {"id": "neon-medium", "label": "Neon (Medium)"},
            {"id": "orch-neon", "label": "Orchestrator (Neon)"},
        ]
        self.deck.catalogs["agy"]["models"] = models
        result = self.send("models", provider="agy")
        self.assertEqual([{"id": "neon-medium", "label": "Neon"}, models[3]], result["items"])
        options = self.send("efforts", provider="agy", model="neon-medium")
        self.assertEqual(["low", "medium", "xhigh"], [item["id"] for item in options["items"]])
        request = {"provider": "agy", "project": self.project, "model": "neon-medium", "prompt": self.prompt}
        self.assertFalse(self.send("start", **request, effort="high")["ok"])
        job = self.send("start", **request, effort="xhigh")["job"]
        self.assertEqual(("neon-xhigh", "xhigh"), (self.deck.jobs[job["id"]]["model"], job["effort"]))
        self.send("stop", job=job["id"])
        repeated = self.send("retry", job=job["id"])["job"]
        self.assertEqual(("neon-xhigh", "xhigh"), (self.deck.jobs[repeated["id"]]["model"], repeated["effort"]))

    def test_codex_uses_only_effort_advertised_for_selected_model(self):
        self.deck.catalogs["codex"]["models"] = [
            {"id": "id-a", "label": "Model A", "efforts": ["low", "high"], "default_effort": "low"},
            {"id": "id-b", "label": "Model B", "efforts": ["medium", "ultra"], "default_effort": "medium"},
        ]
        rows = self.send("efforts", provider="codex", model="id-a")["items"]
        self.assertEqual([("low", "Default"), ("high", "")], [(row["id"], row["detail"]) for row in rows])
        rows_pl = self.send("efforts", provider="codex", model="id-a", lang="pl")["items"]
        self.assertEqual([("low", "Domyślny"), ("high", "")], [(row["id"], row["detail"]) for row in rows_pl])
        request = {"provider": "codex", "project": self.project, "model": "id-a", "prompt": self.prompt}
        self.assertFalse(self.send("start", **request, effort="ultra")["ok"])
        job = self.send("start", **request, effort="high")["job"]
        self.assertEqual(("id-a", "high"), (self.deck.jobs[job["id"]]["model"], job["effort"]))

    def test_language_default_english_and_switch(self):
        settings = self.deck.desktop("settings_get", {})
        self.assertEqual("en", settings["language"])
        saved = self.deck.desktop("settings_save", {"language": "pl"})
        self.assertEqual("pl", saved["language"])
        self.assertEqual("pl", self.send("status")["lang"])
        self.deck.desktop("settings_save", {"language": "en"})
        self.assertEqual("en", self.send("status")["lang"])

    def test_ide_scoped_projects_create_and_rename(self):
        custom_root = Path(self.temp.name) / "ws-root"
        self.deck.desktop("settings_save", {"projects_root": str(custom_root)})

        codex_dir = custom_root / "codex-only"
        agy_dir = custom_root / "agy-only"
        codex_dir.mkdir(parents=True)
        agy_dir.mkdir(parents=True)
        cid = self.deck.desktop("add_project", {"path": str(codex_dir), "provider": "codex"})["id"]
        aid = self.deck.desktop("add_project", {"path": str(agy_dir), "provider": "agy"})["id"]

        codex_list = [p["id"] for p in self.send("projects", provider="codex")["items"]]
        agy_list = [p["id"] for p in self.send("projects", provider="agy")["items"]]
        self.assertIn(cid, codex_list)
        self.assertNotIn(aid, codex_list)
        self.assertIn(aid, agy_list)
        self.assertNotIn(cid, agy_list)

        created = self.send("create_project", provider="codex")["project"]
        self.assertRegex(created["label"], r"^[a-z]+-[a-z]+$")
        created_path = Path(created["path"])
        self.assertTrue(created_path.is_dir())
        self.assertEqual(custom_root.resolve(), created_path.parent)

        saved_prompt = self.deck.desktop("save_prompt", {
            "project": created["id"], "label": "Inspect", "text": "Inspect repo", "provider": "codex",
        })["item"]
        renamed = self.deck.desktop("rename_project", {"project": created["id"], "name": "RenamedProject"})
        self.assertEqual("RenamedProject", renamed["label"])
        self.assertFalse(created_path.exists())
        self.assertTrue(Path(renamed["path"]).is_dir())
        migrated = self.deck.desktop("prompts", {"project": renamed["id"]})["items"]
        self.assertEqual([saved_prompt["id"]], [p["id"] for p in migrated])

    def test_global_prompts_available_in_new_projects(self):
        custom_root = Path(self.temp.name) / "global-ws"
        self.deck.desktop("settings_save", {"projects_root": str(custom_root)})
        global_prompt = self.deck.desktop("save_prompt", {
            "project": "*", "label": "Quick Audit", "text": "Audit project", "provider": "",
        })["item"]
        created = self.send("create_project", provider="agy")["project"]
        listed = self.send("prompts", project=created["id"], provider="agy")["items"]
        self.assertTrue(any(p["id"] == global_prompt["id"] and p["detail"] == "*" for p in listed))
        started = self.send("start", provider="agy", project=created["id"], model="demo", prompt=global_prompt["id"])
        self.assertTrue(started["ok"], started)
        self.send("stop", job=started["job"]["id"])
        self.wait(lambda: not any(j["state"] in ACTIVE for j in self.deck.jobs.values()))

    def test_prompt_description_modes_and_prefixes(self):
        saved = self.deck.desktop("save_prompt", {
            "project": self.project,
            "label": "Build Feature",
            "description": "Implements and verifies feature",
            "text": "Add telemetry",
            "provider": "",
        })["item"]
        self.assertEqual("Implements and verifies feature", saved["description"])
        listed = self.send("prompts", project=self.project, provider="agy")["items"]
        match = next(p for p in listed if p["id"] == saved["id"])
        self.assertEqual("Implements and verifies feature", match["description"])

        self.deck.args.demo = False
        self.addCleanup(lambda: setattr(self.deck.args, "demo", True))
        seen_prompts = []

        def fake_agy(cwd, model, prompt_text, mode, stop, update, **kwargs):
            seen_prompts.append((mode, prompt_text))
            return "finished", "OK"

        with patch.dict("agentdeck.PROVIDERS", {"agy": (lambda **kw: ([], [], []), fake_agy)}):
            for mode in ("read", "write", "plan_execute"):
                started = self.send("start", provider="agy", project=self.project,
                                    model="demo", prompt=saved["id"], mode=mode)
                self.assertTrue(started["ok"], started)
                jid = started["job"]["id"]
                self.deck.tick()
                self.wait(lambda: self.deck.jobs[jid]["state"] == "finished")

        by_mode = dict(seen_prompts)
        self.assertIn("Read-only mode", by_mode["read"])
        self.assertIn("Edit mode", by_mode["write"])
        self.assertIn("Autonomous Plan & Execute mode", by_mode["plan_execute"])

    def test_interactive_question_and_extract_choice(self):
        from providers import extract_choice_question

        q = extract_choice_question(
            "Which database engine should I use?\nA) SQLite\nB) PostgreSQL\nC) JSON file"
        )
        self.assertIsNotNone(q)
        self.assertEqual(3, len(q["options"]))
        self.assertIn("Which database", q["question"])
        self.assertIsNone(extract_choice_question("Summary:\n1. Updated files\n2. Ran tests\nAll green."))

        self.deck.args.demo = False
        self.addCleanup(lambda: setattr(self.deck.args, "demo", True))
        captured_choice = {}

        def fake_codex(cwd, model, prompt_text, mode, stop, update, **kwargs):
            ans = kwargs["ask_user"](q)
            captured_choice["value"] = ans["label"] if ans else None
            return "finished", "Done"

        with patch.dict("agentdeck.PROVIDERS", {"codex": (lambda **kw: ([], [], []), fake_codex)}):
            started = self.send("start", provider="codex", project=self.project,
                                model="demo", prompt=self.prompt, mode="plan_execute")
            jid = started["job"]["id"]
            self.deck.tick()
            self.wait(lambda: self.deck.jobs[jid]["state"] == "waiting_input")
            job_view = self.send("job", job=jid)["job"]
            self.assertEqual("waiting_input", job_view["state"])
            self.assertEqual(3, len(job_view["question"]["options"]))
            ans_res = self.send("answer", job=jid, choice=1)
            self.assertTrue(ans_res["ok"], ans_res)
            self.wait(lambda: self.deck.jobs[jid]["state"] == "finished")
            self.assertEqual("B) PostgreSQL", captured_choice["value"])

    def test_codex_account_vault_save_switch_delete(self):
        fake_auth = Path(self.temp.name) / "fake_auth.json"
        with patch.object(self.deck, "_codex_auth_path", return_value=fake_auth):
            fake_auth.write_bytes(b'{"account":"alpha"}')
            with patch("agentdeck.codex_account", return_value={"email": "alpha@example.com", "plan": "plus", "user_id": "u1"}):
                saved_a = self.deck.desktop("save_account", {"provider": "codex", "label": "Alpha"})["item"]
            fake_auth.write_bytes(b'{"account":"beta"}')
            with patch("agentdeck.codex_account", return_value={"email": "beta@example.com", "plan": "pro", "user_id": "u2"}):
                saved_b = self.deck.desktop("save_account", {"provider": "codex", "label": "Beta"})["item"]

            self.assertEqual(b'{"account":"beta"}', fake_auth.read_bytes())
            switched = self.send("switch_account", provider="codex", account=saved_a["id"])
            self.assertTrue(switched["ok"], switched)
            self.assertEqual(b'{"account":"alpha"}', fake_auth.read_bytes())

            listed = self.send("accounts", provider="codex")["items"]
            alpha_row = next(r for r in listed if r["id"] == saved_a["id"])
            self.assertEqual("active", alpha_row["state"])

            self.deck.desktop("delete_account", {"provider": "codex", "account": saved_b["id"]})
            listed_after = self.send("accounts", provider="codex")["items"]
            self.assertEqual([saved_a["id"]], [r["id"] for r in listed_after])

    def test_local_relay_start_and_stop(self):
        links = Links(self.deck.directory, self.deck, threading.Event())
        device = secrets.token_hex(12)
        links.save(device, "127.0.0.1", secrets.token_bytes(32))
        started = links.local_relay_start("127.0.0.1", 0)
        self.assertTrue(started["running"])
        self.assertGreater(started["port"], 0)
        stopped = links.local_relay_stop()
        self.assertFalse(stopped["running"])

    def test_finished_answer_is_paged_without_truncating_unicode(self):
        ident = self.launch()
        result_dir = self.deck.directory / "demo-results"
        result_dir.mkdir()
        (result_dir / (ident + ".txt")).write_text("ą" * 420 + "koniec", encoding="utf-8")
        self.deck.jobs[ident]["state"] = "finished"
        first = self.send("job", job=ident, output_page=0)["output"]
        second = self.send("job", job=ident, output_page=1)["output"]
        self.assertEqual("ą" * 420, first["text"])
        self.assertTrue(first["more"])
        self.assertEqual({"page": 1, "text": "koniec", "more": False}, second)
        details = self.deck.desktop("session_details", {"job": ident})
        self.assertEqual("ą" * 420 + "koniec", details["answer"])
        self.assertEqual(str(result_dir / (ident + ".txt")), details["output_path"])

    def test_internet_relay_delivers_authenticated_encrypted_frames(self):
        import hmac
        from urllib.error import HTTPError
        from urllib.request import ProxyHandler, Request, build_opener
        from links import channel, encode, relay_auth, relay_channel
        from relay import make_server

        opener = build_opener(ProxyHandler({}))
        key = secrets.token_bytes(32)
        route, auth = relay_channel(key), relay_auth(key)
        server = make_server(("127.0.0.1", 0), {route: auth})
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        endpoint = "http://127.0.0.1:" + str(server.server_port)
        links = Links(self.deck.directory, self.deck, threading.Event())
        device = secrets.token_hex(12)
        self.assertEqual({}, links.exchange_remote(device, endpoint, key, {"answer": None}))
        url = endpoint + "/v1/" + route + "/stick"

        def post(content=b"", request_nonce=None, signature=None):
            nonce = request_nonce or secrets.token_bytes(12)
            mac = signature or hmac.digest(auth, b"stick" + nonce + content, "sha256").hex()
            return opener.open(Request(url, data=content, headers={"X-AgentDeck-Nonce": nonce.hex(),
                                                                   "X-AgentDeck-Auth": mac}), timeout=2)

        with self.assertRaises(HTTPError) as invalid:
            post(signature="0" * 64)
        self.assertEqual(403, invalid.exception.code)
        with post() as reply:
            pull = reply.read()
        self.assertTrue(pull.startswith(b"AD2Q"))
        request_nonce = pull[16:28]
        iv = secrets.token_bytes(12)
        answer = {"device": device, "request": {"op": "models", "provider": "codex"}}
        _, encrypt = channel(key)
        packet = request_nonce + b"AD2R" + iv + encrypt.encrypt(iv, encode(answer), request_nonce)
        unique = secrets.token_bytes(12)
        with post(packet, unique) as reply:
            self.assertEqual(pull, reply.read())
        with post(packet, unique) as reply:
            self.assertEqual(b"", reply.read())
        self.assertEqual(answer, links.exchange_remote(device, endpoint, key, {"answer": None}))

    def test_desktop_prompts_are_project_and_provider_scoped(self):
        stop = threading.Event()
        links = Links(self.deck.directory, self.deck, stop)
        server, marker = desktop_server(self.deck, links)
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        second_folder = self.deck.directory / "another-project"
        second_folder.mkdir()
        other = desktop_request(self.deck.directory, "add_project", path=str(second_folder),
                                agy_project="agy-local-project")["id"]
        private = desktop_request(self.deck.directory, "save_prompt", project=other,
                                  provider="agy", label="AGY Only", text="Content ąę")["item"]["id"]
        self.assertEqual([], self.send("prompts", project=other, provider="codex")["items"])
        self.assertEqual([private], [p["id"] for p in self.send("prompts", project=other, provider="agy")["items"]])
        self.assertFalse(self.send("start", project=self.project, provider="agy", model="demo",
                                   prompt=private)["ok"])
        desktop_request(self.deck.directory, "delete_prompt", project=other, id=private)
        self.assertEqual([], desktop_request(self.deck.directory, "prompts", project=other)["items"])
        self.assertTrue(marker.is_file())

    def test_pairing_and_relay_crypto(self):
        import json

        from cryptography.exceptions import InvalidTag
        from links import PAIR, PULL, RESPONSE, channel, encode, make_packet, pairing_key, read_response

        key = pairing_key("2345-6789-ABCD-EFGH")
        self.assertEqual(key, pairing_key("23456789abcdefgh"))
        for kind, ident in ((PAIR, b""), (PULL, secrets.token_bytes(12))):
            out, wire = make_packet(kind, ident, {"text": "Zażółć"}, key)
            pc, stick = channel(key)
            header = kind + ident
            plain = pc.decrypt(out, wire[len(header) + 12:], header)
            self.assertEqual("Zażółć", json.loads(plain)["text"])
            made = secrets.token_bytes(12)
            reply = RESPONSE + made + stick.encrypt(made, encode({"ok": True}), out)
            self.assertTrue(read_response(reply, out, key)["ok"])
            with self.assertRaises(InvalidTag):
                read_response(reply, secrets.token_bytes(12), key)

    def test_usb_code_pairing_and_lan_management_use_same_device(self):
        stop = threading.Event()
        links = Links(self.deck.directory, self.deck, stop)
        device = secrets.token_hex(12)
        links.observe({"device": device, "screen": "AgentDeck"}, usb=True)
        messages = []

        def usb_reply():
            pair = links.usb.get(timeout=2)
            messages.append(pair.request)
            pair.answer = {"id": pair.request["id"], "ok": True, "device": device, "address": "127.0.0.1"}
            pair.ready.set()

        worker = threading.Thread(target=usb_reply)
        worker.start()
        with patch.object(links, "interface_snapshot", return_value=[]):
            self.assertEqual(device, links.pair_usb("2345-6789-ABCD-EFGH"))
        worker.join(timeout=2)
        self.assertEqual("23456789ABCDEFGH", messages[0]["code"])
        self.assertEqual(device, links.paired()[0]["id"])
        links.usb_ready = False
        ticket = secrets.token_hex(12)
        requests = []

        def simulate(address, kind, header, key, payload, **options):
            reply = {"device": device, "selected": True, "ticket": ticket,
                     "snapshot": {"device": device, "screen": "Wi-Fi", "wifi_connected": True}}
            if command := payload.get("control"):
                requests.append(command)
                reply["control_result"] = {"id": command["id"], "ok": True, "target_revision": 2}
            return reply

        with patch.object(links, "scan", return_value={}), patch.object(links, "exchange", side_effect=simulate):
            worker = threading.Thread(target=links.run, daemon=True)
            worker.start()
            try:
                self.wait(lambda: links.selected.get(device))
                result = links.command(device, "wifi_scan")
                self.assertEqual(2, result["target_revision"])
                self.assertEqual(ticket, requests[0]["ticket"])
                self.assertEqual("wifi_scan", requests[0]["action"])
                self.assertEqual("Wi-Fi", links.devices()["devices"][0]["snapshot"]["screen"])
            finally:
                stop.set()
                worker.join(timeout=2)

    def test_session_persists_after_leaving_job_view(self):
        self.deck.args.demo = False
        self.addCleanup(lambda: setattr(self.deck.args, "demo", True))
        gate = threading.Event()

        def fake_run(cwd, model, prompt_text, mode, stop, update, **kwargs):
            update("Working in background")
            gate.wait(3)
            return "finished", "Persistent result 123"

        with patch.dict("agentdeck.PROVIDERS", {"codex": (lambda **kw: ([], [], []), fake_run)}):
            started = self.send("start", provider="codex", project=self.project, model="demo", prompt=self.prompt)
            jid = started["job"]["id"]
            self.deck.tick()
            self.wait(lambda: self.deck.jobs[jid]["state"] == "running")

            # Simulate user backing out of Page::Job to Home/Sessions
            self.assertEqual(1, self.send("status")["active"])
            sessions = self.send("sessions")["items"]
            self.assertEqual(jid, sessions[0]["id"])
            self.assertEqual("running", sessions[0]["state"])

            gate.set()
            self.wait(lambda: self.deck.jobs[jid]["state"] == "finished")

        sessions_done = self.send("sessions")["items"]
        self.assertEqual("finished", sessions_done[0]["state"])
        job_reopened = self.send("job", job=jid, output_page=0)
        self.assertEqual("finished", job_reopened["job"]["state"])
        self.assertEqual("Persistent result 123", job_reopened["output"]["text"])

    def test_codex_account_auto_saved(self):
        fake_auth = Path(self.temp.name) / "auto_auth.json"
        self.deck.args.demo = False
        self.addCleanup(lambda: setattr(self.deck.args, "demo", True))
        with patch.object(self.deck, "_codex_auth_path", return_value=fake_auth):
            fake_auth.write_bytes(b'{"account":"gamma"}')
            with patch("agentdeck.codex_account", return_value={"email": "gamma@example.com", "plan": "plus", "user_id": "u3"}), \
                 patch("agentdeck.agy_account", return_value={}):
                listed = self.deck.desktop("accounts", {})
            profiles = listed["codex"]["profiles"]
            self.assertEqual(1, len(profiles))
            self.assertTrue(profiles[0]["active"])
            self.assertEqual("gamma@example.com", profiles[0]["email"])

            # Removing dismissed active hash prevents auto-readd until another login
            self.deck.desktop("delete_account", {"provider": "codex", "account": profiles[0]["id"]})
            with patch("agentdeck.codex_account", return_value={"email": "gamma@example.com", "plan": "plus", "user_id": "u3"}), \
                 patch("agentdeck.agy_account", return_value={}):
                listed_after = self.deck.desktop("accounts", {})
            self.assertEqual([], listed_after["codex"]["profiles"])

    def test_agy_subscription_parsing(self):
        from providers import parse_agy_subscription

        pro_usage = {
            "command": {
                "data": {
                    "groups": [
                        {
                            "name": "Gemini Models",
                            "description": "Models within this group: Gemini 4 Neon, Gemini 4 Hydrogen",
                            "buckets": [{"window": "weekly", "remaining_fraction": 0.88}],
                        },
                        {
                            "name": "Claude and GPT models",
                            "buckets": [{"window": "weekly", "remaining_fraction": 0.39}],
                        },
                        {
                            "name": "Gemini 4 Argon",
                            "description": "For paid Pro plans, Argon quota is separate.",
                            "buckets": [{"window": "weekly", "remaining_fraction": 1.0}],
                        },
                    ]
                }
            }
        }
        credits_json = {"command": {"data": {"remaining_credits": 0, "upgrade_uri": "https://antigravity.google/g1-upgrade"}}}
        parsed = parse_agy_subscription(pro_usage, credits_json)
        self.assertEqual("Pro", parsed["plan"])
        self.assertEqual(0, parsed["credits"])
        self.assertEqual(88, parsed["gemini_weekly"])
        self.assertEqual(39, parsed["third_party_weekly"])
        self.assertIn("Gemini 88%", parsed["quota_summary"])
        self.assertIn("Claude/GPT 39%", parsed["quota_summary"])

        ultra_usage = {
            "command": {
                "data": {
                    "groups": [
                        {
                            "name": "Gemini Models",
                            "description": "Includes Higher Argon quota for all Ultra models",
                            "buckets": [{"window": "weekly", "remaining_fraction": 0.95}],
                        }
                    ]
                }
            }
        }
        self.assertEqual("Ultra", parse_agy_subscription(ultra_usage, None)["plan"])

    def test_settings_onboarded_flag(self):
        with patch("agentdeck.autostart_status", return_value=False), \
             patch("agentdeck.set_autostart", return_value=True):
            initial = self.deck.desktop("settings_get", {})
            self.assertFalse(initial["onboarded"])
            updated = self.deck.desktop("settings_save", {"onboarded": True, "autostart": True})
            self.assertTrue(updated["onboarded"])
            reloaded = self.deck._load_settings()
            self.assertTrue(reloaded["onboarded"])

    def test_updater_version_comparison_and_github_release_check(self):
        from updater import check_for_update, is_newer, parse_version

        self.assertEqual((0, 7, 1), parse_version("v0.7.1"))
        self.assertEqual((1, 0, 0), parse_version("v1.0"))
        self.assertTrue(is_newer("v0.7.0", "0.6.0"))
        self.assertFalse(is_newer("v0.6.0", "0.6.0"))
        self.assertFalse(is_newer("v0.5.9", "0.6.0"))

        fake_release = {
            "tag_name": "v0.7.0",
            "name": "AgentDeck v0.7.0",
            "body": "Faster Wi-Fi sync",
            "published_at": "2026-11-22T10:00:00Z",
            "assets": [
                {
                    "name": "AgentDeck-StickS3.bin",
                    "browser_download_url": "https://github.com/natekk8/agentdeck/releases/download/v0.7.0/AgentDeck-StickS3.bin",
                    "size": 1100000,
                },
                {
                    "name": "AgentDeck-Setup-0.7.0.exe",
                    "browser_download_url": "https://github.com/natekk8/agentdeck/releases/download/v0.7.0/AgentDeck-Setup-0.7.0.exe",
                    "size": 3100000,
                },
            ],
        }
        with patch("updater._fetch_json", return_value=fake_release):
            res = check_for_update(current_version="0.6.0", repo="natekk8/agentdeck")
        self.assertTrue(res["available"])
        self.assertEqual("0.7.0", res["latest_version"])
        self.assertEqual("AgentDeck-Setup-0.7.0.exe", res["asset_name"])
        self.assertIn("releases/download/v0.7.0/AgentDeck-Setup-0.7.0.exe", res["asset_url"])

        with patch("agentdeck.check_for_update", return_value=res):
            desktop_res = self.deck.desktop("update_check", {})
            self.assertTrue(desktop_res["available"])

    def test_glitchtip_reporter_parsing_sanitization_and_submit(self):
        import json
        from reporter import parse_dsn, sanitize_text, send_glitchtip_report

        parsed = parse_dsn("https://abc123key@app.glitchtip.com/42")
        self.assertEqual("https://app.glitchtip.com/api/42/store/", parsed["store_url"])
        self.assertIn("sentry_key=abc123key", parsed["auth_header"])

        redacted = sanitize_text(r"Failed reading C:\Users\Natanek\secret.txt Bearer supersecret123456")
        self.assertNotIn("Natanek", redacted)
        self.assertNotIn("supersecret123456", redacted)

        captured = {}

        class FakeResp:
            status = 200
            def __enter__(self):
                return self
            def __exit__(self, *args):
                return False
            def read(self):
                return b'{"id":"deadbeef1234"}'

        def fake_urlopen(req, timeout=12):
            captured["url"] = req.full_url
            captured["auth"] = req.get_header("X-sentry-auth")
            captured["body"] = json.loads(req.data.decode("utf-8"))
            return FakeResp()

        with patch("reporter.urlopen", side_effect=fake_urlopen):
            self.deck.desktop("settings_save", {"glitchtip_dsn": "https://pub999@app.glitchtip.com/7"})
            res = self.deck.desktop("report_submit", {
                "kind": "feature",
                "title": "Add dark tray icon",
                "description": r"Testing path C:\Users\Bob\file.txt in feature idea.",
                "contact": "@natekk8",
                "include_logs": True,
            })
        self.assertTrue(res["sent"])
        self.assertEqual("deadbeef1234", res["event_id"])
        self.assertEqual("https://app.glitchtip.com/api/7/store/", captured["url"])
        self.assertEqual("feature", captured["body"]["tags"]["report_kind"])
        self.assertNotIn("Bob", captured["body"]["extra"]["description"])

    def test_sync_settings_from_stick_updates_windows_language(self):
        self.assertEqual("en", self.deck.desktop("settings_get", {})["language"])
        res = self.send("sync_settings", lang="pl")
        self.assertTrue(res["ok"], res)
        self.assertEqual("pl", res["lang"])
        self.assertEqual("pl", self.deck.desktop("settings_get", {})["language"])

    def test_last_seen_version_persists(self):
        saved = self.deck.desktop("settings_save", {"last_seen_version": "2.0.0"})
        self.assertEqual("2.0.0", saved["last_seen_version"])
        loaded = self.deck.desktop("settings_get", {})
        self.assertEqual("2.0.0", loaded["last_seen_version"])
        self.assertEqual("2.0.0", loaded["version"])
        self.assertEqual("v2.0", loaded["display_version"])

    def test_devices_reports_firmware_update_needed(self):
        links = Links(Path(self.temp.name), self.deck, lambda *a: None)
        dev_id = "aabbccddeeff001122334455"
        links.observe({"device": dev_id, "firmware": "1.0.0", "battery": 88}, usb=True)
        info = links.devices()
        self.assertEqual("2.0.0", info["expected_firmware"])
        self.assertEqual("1.0.0", info["stick_firmware"])
        self.assertTrue(info["firmware_update_needed"])

        links.observe({"device": dev_id, "firmware": "2.0.0", "battery": 88}, usb=True)
        info2 = links.devices()
        self.assertEqual("2.0.0", info2["stick_firmware"])
        self.assertFalse(info2["firmware_update_needed"])

    def test_updater_download_and_apply_script(self):
        import io
        import time
        from updater import Updater

        data_dir = Path(self.temp.name)
        updater = Updater(data_dir, data_dir)
        payload = b"MZ_FAKE_INNO_INSTALLER_BINARY" * 16

        class FakeDownloadResp(io.BytesIO):
            def __init__(self, data):
                super().__init__(data)
                self.headers = {"Content-Length": str(len(data))}
            def __enter__(self):
                return self
            def __exit__(self, *args):
                return False

        with patch("updater.urlopen", return_value=FakeDownloadResp(payload)):
            status = updater.start_download(
                "https://github.com/natekk8/agentdeck/releases/download/v1.0.1/AgentDeck-Setup-1.0.1.exe",
                "AgentDeck-Setup-1.0.1.exe",
                target_version="1.0.1",
            )
            self.assertEqual("downloading", status["status"])
            for _ in range(40):
                st = updater.snapshot()
                if st["status"] == "ready":
                    break
                time.sleep(0.02)
            st = updater.snapshot()
            self.assertEqual("ready", st["status"])
            self.assertTrue(Path(st["installer_path"]).exists())
            self.assertEqual(payload, Path(st["installer_path"]).read_bytes())

            with patch("updater.subprocess.Popen") as popen_mock:
                res = updater.apply_update()
                self.assertTrue(res["launching_installer"])
                self.assertTrue(popen_mock.called)
                ps_path = Path(st["installer_path"]).parent / "apply-update.ps1"
                self.assertTrue(ps_path.exists())
                ps_text = ps_path.read_text(encoding="utf-8")
                self.assertIn("/VERYSILENT", ps_text)
                self.assertIn("Remove-Item -LiteralPath", ps_text)
                self.assertIn("Start-Process -FilePath", ps_text)

    def test_delete_and_clear_sessions(self):
        j1 = self.launch("codex")
        j2 = self.launch("agy")
        self.deck.tick()
        self.wait(lambda: self.deck.jobs[j1]["state"] == "finished" and self.deck.jobs[j2]["state"] == "finished")
        self.assertTrue(self.deck.result_path(j1).exists())

        res1 = self.deck.desktop("delete_session", {"job": j1})
        self.assertEqual(j1, res1["deleted"])
        self.assertNotIn(j1, self.deck.jobs)
        self.assertFalse(self.deck.result_path(j1).exists())

        res2 = self.deck.desktop("clear_sessions", {})
        self.assertEqual(1, res2["deleted"])
        self.assertNotIn(j2, self.deck.jobs)

    def test_home_ide_status_overview_and_quick_prompt(self):
        ides = self.deck.desktop("ide_status", {})["ides"]
        self.assertTrue(ides["agy"]["installed"])
        self.assertTrue(ides["codex"]["installed"])
        ov, _ = self.deck.overview()
        self.assertTrue(ov["has_agy"])
        self.assertTrue(ov["has_codex"])

        started = self.deck.desktop("start_prompt", {
            "provider": "agy",
            "project": self.project,
            "text": "Quick audit test",
            "mode": "read",
        })
        jid = started["job"]["id"]
        self.wait(lambda: self.deck.jobs[jid]["state"] == "finished")
        details = self.deck.desktop("session_details", {"job": jid})
        self.assertEqual("finished", details["state"])

    def test_ide_install_refreshes_catalog_with_force(self):
        self.deck.catalogs["codex"] = {"models": [{"id": "old", "label": "Old"}], "limits": [], "expiry": time.monotonic() + 300}
        fake_ides = {
            "agy": {"id": "agy", "name": "Antigravity CLI", "installed": True, "path": "agy", "version": "1.0"},
            "codex": {"id": "codex", "name": "ChatGPT Codex", "installed": True, "path": "codex", "version": "1.0"},
        }
        with patch("agentdeck.ide_install", return_value=fake_ides) as install_mock:
            res = self.deck.desktop("ide_install", {"provider": "codex"})
        self.assertTrue(install_mock.called)
        self.assertEqual(fake_ides, res["ides"])
        self.wait(lambda: "codex" not in self.deck.pending)
        self.assertEqual("demo", self.deck.catalogs["codex"]["models"][0]["id"])

    def test_updater_rejects_untrusted_url_and_sanitizes_filename(self):
        import io
        from updater import Updater

        data_dir = Path(self.temp.name)
        updater = Updater(data_dir, data_dir)
        with self.assertRaises(ValueError):
            updater.start_download("http://github.com/evil.exe", "evil.exe")
        with self.assertRaises(ValueError):
            updater.start_download("https://evil.example.com/setup.exe", "setup.exe")

        class FakeResp(io.BytesIO):
            def __init__(self, data):
                super().__init__(data)
                self.headers = {"Content-Length": str(len(data))}
            def __enter__(self):
                return self
            def __exit__(self, *args):
                return False

        with patch("updater.urlopen", return_value=FakeResp(b"MZ")):
            st = updater.start_download(
                "https://github.com/natekk8/agentdeck/releases/download/v1.1.0/AgentDeck.exe",
                "../../..",
            )
            self.wait(lambda: updater.snapshot()["status"] == "ready")
        final = updater.snapshot()
        self.assertEqual(data_dir.name, data_dir.name)
        self.assertTrue(Path(final["installer_path"]).name.endswith(".exe"))
        self.assertFalse(Path(final["installer_path"]).name.startswith("."))

    def test_reporter_collect_diagnostics_reads_tail_and_sanitizes(self):
        from reporter import collect_diagnostics

        data_dir = Path(self.temp.name)
        huge_log = ("A" * 20000) + r"\nError in C:\Users\SecretUser\file.py with Bearer secret_token_123456"
        (data_dir / "bridge-errors.log").write_text(huge_log, encoding="utf-8")
        diag = collect_diagnostics(data_dir)
        self.assertIn("bridge-errors.log", diag)
        self.assertIn("<user>", diag)
        self.assertIn("<redacted>", diag)
        self.assertNotIn("SecretUser", diag)
        self.assertNotIn("secret_token_123456", diag)

    def test_relay_rejects_oversized_packet_and_invalid_magic(self):
        from urllib.error import HTTPError
        from urllib.request import ProxyHandler, Request, build_opener
        from links import relay_auth, relay_channel
        from relay import MAX_PACKET, make_server

        opener = build_opener(ProxyHandler({}))
        key = secrets.token_bytes(32)
        route, auth = relay_channel(key), relay_auth(key)
        server = make_server(("127.0.0.1", 0), {route: auth})
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        url = f"http://127.0.0.1:{server.server_port}/v1/{route}/pc"

        with self.assertRaises(HTTPError) as oversized:
            opener.open(Request(url, data=b"X" * (MAX_PACKET + 1), headers={
                "X-AgentDeck-Nonce": "0" * 24,
                "X-AgentDeck-Auth": "0" * 64,
            }), timeout=2)
        self.assertEqual(413, oversized.exception.code)

        with self.assertRaises(HTTPError) as bad_magic:
            opener.open(Request(url, data=b"BAD!" + b"0" * 40, headers={
                "X-AgentDeck-Nonce": "0" * 24,
                "X-AgentDeck-Auth": "0" * 64,
            }), timeout=2)
        self.assertEqual(400, bad_magic.exception.code)

    def test_parse_agy_subscription_handles_edge_cases(self):
        from providers import parse_agy_subscription

        res = parse_agy_subscription(
            {"groups": [{"name": "Other", "buckets": []}]},
            {"remaining_credits": float("nan")},
        )
        self.assertEqual("Standard", res["plan"])
        self.assertIsNone(res["credits"])

    def test_job_output_handles_invalid_utf8_bytes(self):
        ident = self.launch()
        result_dir = self.deck.directory / "demo-results"
        result_dir.mkdir(exist_ok=True)
        (result_dir / (ident + ".txt")).write_bytes(b"Hello \xff\xfe world")
        self.deck.jobs[ident]["state"] = "finished"
        res = self.send("job", job=ident, output_page=0)
        self.assertTrue(res["ok"])
        self.assertIn("Hello", res["output"]["text"])
        self.assertIn("world", res["output"]["text"])

    def test_prompt_custom_order_and_reorder_syncs_to_stick(self):
        p2 = self.deck.desktop("save_prompt", {
            "project": self.project, "label": "Zebra", "text": "Last alphabetically", "provider": "",
        })["item"]["id"]
        p3 = self.deck.desktop("save_prompt", {
            "project": self.project, "label": "Alpha", "text": "First alphabetically", "provider": "",
        })["item"]["id"]
        initial = self.send("prompts", project=self.project, provider="codex")["items"]
        self.assertEqual([self.prompt, p2, p3], [item["id"] for item in initial])
        self.assertEqual([1, 2, 3], [item["order"] for item in initial])

        reordered = self.deck.desktop("reorder_prompts", {
            "project": self.project,
            "order": [p3, self.prompt, p2],
        })["items"]
        self.assertEqual([p3, self.prompt, p2], [item["id"] for item in reordered])

        stick_items = self.send("prompts", project=self.project, provider="codex")["items"]
        self.assertEqual([p3, self.prompt, p2], [item["id"] for item in stick_items])
        self.assertEqual([1, 2, 3], [item["order"] for item in stick_items])

        # Updating an existing prompt preserves its slot rather than moving it to the end
        self.deck.desktop("save_prompt", {
            "id": self.prompt, "project": self.project, "label": "Test Updated",
            "text": "Updated body", "provider": "",
        })
        stick_after_edit = self.send("prompts", project=self.project, provider="codex")["items"]
        self.assertEqual([p3, self.prompt, p2], [item["id"] for item in stick_after_edit])

    def test_prompt_chain_executes_sequentially_and_cancels_on_failure(self):
        p2 = self.deck.desktop("save_prompt", {
            "project": self.project, "label": "Step Two", "text": "Verify step 2", "provider": "",
        })["item"]["id"]
        with patch.dict(os.environ, {"AGENTDECK_FAST_DEMO": "1"}):
            res = self.send("chain_start", steps=[
                {"provider": "codex", "project": self.project, "model": "demo", "prompt": self.prompt},
                {"provider": "codex", "project": self.project, "model": "demo", "prompt": p2},
            ])
            self.assertTrue(res["ok"], res)
            self.assertEqual(2, len(res["jobs"]))
            j1, j2 = res["jobs"][0]["id"], res["jobs"][1]["id"]
            self.assertEqual(1, self.deck.jobs[j1]["chain_index"])
            self.assertEqual(2, self.deck.jobs[j2]["chain_index"])
            self.assertEqual(j1, self.deck.jobs[j2]["depends_on"])

            # Step 2 stays queued while step 1 runs
            self.deck.tick()
            self.assertIn(self.deck.jobs[j1]["state"], ("queued", "running", "finished"))
            if self.deck.jobs[j1]["state"] != "finished":
                self.assertEqual("queued", self.deck.jobs[j2]["state"])

            self.wait(lambda: self.deck.jobs[j2]["state"] == "finished", seconds=15)
            self.assertEqual("finished", self.deck.jobs[j1]["state"])
            self.assertLessEqual(self.deck.jobs[j1]["finished"], self.deck.jobs[j2]["started"])

            # Appending after a finished/running step links a new step to that chain
            appended = self.send("chain_append", after_job=j2, provider="codex", project=self.project, model="demo", prompt=self.prompt)
            self.assertTrue(appended["ok"], appended)
            j3 = appended["job"]["id"]
            self.assertEqual(res["chain_id"], self.deck.jobs[j3]["chain_id"])
            self.wait(lambda: self.deck.jobs[j3]["state"] == "finished", seconds=15)

            # Cancelling step 1 automatically cancels dependent step 2 without running it
            fail_res = self.send("chain_start", steps=[
                {"provider": "codex", "project": self.project, "model": "demo", "prompt": self.prompt},
                {"provider": "codex", "project": self.project, "model": "demo", "prompt": p2},
            ])
            s1, s2 = fail_res["jobs"][0]["id"], fail_res["jobs"][1]["id"]
            self.assertTrue(self.send("stop", job=s1)["ok"])
            self.wait(lambda: self.deck.jobs[s1]["state"] == "cancelled", seconds=8)
            self.deck.tick()
            self.wait(lambda: self.deck.jobs[s2]["state"] == "cancelled", seconds=8)
            self.assertEqual(0, self.deck.jobs[s2].get("started"))

    def test_shutdown_after_work_waits_for_chain_and_disarms_on_cancel(self):
        p2 = self.deck.desktop("save_prompt", {
            "project": self.project, "label": "Chain Shutdown Step", "text": "Finish up", "provider": "",
        })["item"]["id"]
        with patch.dict(os.environ, {"AGENTDECK_FAST_DEMO": "1"}):
            res = self.send("chain_start", steps=[
                {"provider": "codex", "project": self.project, "model": "demo", "prompt": self.prompt},
                {"provider": "codex", "project": self.project, "model": "demo", "prompt": p2},
            ], shutdown_after=True)
            self.assertTrue(res["ok"], res)
            self.assertTrue(res["shutdown_after"])
            self.assertFalse(res["armed"])

            j1, j2 = res["jobs"][0]["id"], res["jobs"][1]["id"]
            self.wait(lambda: self.deck.jobs[j1]["state"] == "finished", seconds=15)
            self.assertTrue(self.deck.shutdown_after_work)
            self.assertFalse(self.deck.power_armed)

            self.wait(lambda: self.deck.jobs[j2]["state"] == "finished", seconds=15)
            self.deck.tick()
            self.assertIsNotNone(self.deck.shutdown_settle_until)
            self.deck.shutdown_settle_until = time.monotonic() - 0.1
            self.deck.tick()
            self.assertTrue(self.deck.power_armed)
            self.assertFalse(self.deck.shutdown_after_work)

            cancelled = self.send("shutdown_after", enabled=False)
            self.assertTrue(cancelled["ok"])
            self.assertFalse(cancelled["shutdown_after"])
            self.assertFalse(cancelled["armed"])

    def test_codex_v2_events_approvals_and_anti_premature_exit(self):
        self.assertIsNone(providers.extract_choice_question("Done!\n1. Updated Button.cs\n2. Ran unit tests"))
        q = providers.extract_choice_question("Which strategy should I use?\n1. Safe refactor\n2. Fast patch")
        self.assertIsNotNone(q)
        self.assertEqual(2, len(q["options"]))

        sent_payloads = []
        event_queue = [
            {"id": 99, "method": "item/commandExecution/requestApproval", "params": {"command": "pytest -q"}},
            {"method": "item/started", "params": {"item": {"id": "cmd-1", "type": "commandExecution", "command": "pytest -q", "cwd": self.temp.name}}},
            {"method": "item/started", "params": {"item": {"id": "fc-1", "type": "fileChange", "changes": [{"path": "host/app.py"}]}}},
            {"method": "item/started", "params": {"item": {"id": "mcp-1", "type": "mcpToolCall", "server": "github", "tool": "list_prs"}}},
            {"method": "item/started", "params": {"item": {"id": "web-1", "type": "webSearch", "query": "codex app-server"}}},
            {"method": "item/started", "params": {"item": {"id": "sub-1", "type": "collabAgentToolCall", "tool": "spawn_agent", "prompt": "Audit module"}}},
            {"method": "item/commandExecution/outputDelta", "params": {"itemId": "cmd-1", "delta": "2 passed in 0.4s\n"}},
            # turn/completed arrives while collabAgentToolCall sub-1 is still active -> must NOT break early!
            {"method": "turn/completed", "params": {"turn": {"id": "turn-1", "status": "completed"}}},
            {"method": "item/completed", "params": {"item": {"id": "cmd-1", "type": "commandExecution", "command": "pytest -q", "exitCode": 0, "aggregatedOutput": "2 passed"}}},
            {"method": "item/completed", "params": {"item": {"id": "fc-1", "type": "fileChange", "changes": [{"path": "host/app.py"}], "status": "done"}}},
            {"method": "item/completed", "params": {"item": {"id": "mcp-1", "type": "mcpToolCall", "server": "github", "tool": "list_prs", "status": "done"}}},
            {"method": "item/completed", "params": {"item": {"id": "web-1", "type": "webSearch", "query": "codex app-server", "status": "done"}}},
            {"method": "item/completed", "params": {"item": {"id": "sub-1", "type": "collabAgentToolCall", "tool": "spawn_agent", "agentsStates": {"a1": "done"}}}},
            {"method": "item/completed", "params": {"item": {"id": "msg-1", "type": "agentMessage", "text": "All checks passed cleanly."}}},
            {"method": "thread/status/changed", "params": {"status": {"type": "idle"}}},
            {},
        ]

        class FakeRpc:
            def __init__(self, *args, on_request=None, **kwargs):
                self.on_request = on_request
                self.deferred = []
                self.proc = SimpleNamespace(pid=12345)

            def __enter__(self):
                return self

            def __exit__(self, *exc):
                return False

            def call(self, method, params=None, seconds=15):
                if method == "windowsSandbox/readiness":
                    return {"status": "ready"}
                if method == "thread/start":
                    return {"thread": {"id": "th-1"}}
                if method == "turn/start":
                    return {"turn": {"id": "turn-1"}}
                return {}

            def send(self, obj):
                sent_payloads.append(obj)

            def receive(self):
                while event_queue:
                    ev = event_queue.pop(0)
                    if "id" in ev and "method" in ev and self.on_request:
                        self.on_request(self, ev)
                        continue
                    return ev
                return {}

        updates = []
        with patch.object(providers, "Rpc", FakeRpc):
            status, out = providers.codex_run(
                self.temp.name, "gpt-5.4", "Verify repo", "write", threading.Event(),
                lambda detail, **kw: updates.append((detail, kw)),
            )

        self.assertEqual("finished", status)
        self.assertIn("All checks passed cleanly.", out)
        self.assertTrue(any(p.get("id") == 99 and p.get("result", {}).get("decision") == "accept" for p in sent_payloads))
        phases = {kw.get("phase") for _, kw in updates if kw.get("phase")}
        self.assertTrue({"command", "file_edit", "mcp", "browser", "subagent", "finishing", "reply"}.issubset(phases))
        self.assertTrue(any("2 passed" in kw.get("live_output", "") for _, kw in updates))

    def test_agy_stream_json_tools_browser_subagents_and_flags(self):
        tpath = Path(self.temp.name) / "transcript.jsonl"
        tpath.write_text(json.dumps({
            "step_index": 9, "type": "SYSTEM_MESSAGE", "status": "DONE",
            "content": "Subagent finished browser verification",
        }) + "\n", encoding="utf-8")
        old_time = time.time() - 10
        os.utime(tpath, (old_time, old_time))

        captured_args = []
        stream_lines = [
            json.dumps({"event": "init", "conversation_id": "conv-xyz"}) + "\n",
            json.dumps({"event": "step_update", "step_update": {"step_index": 1, "run_command": {"CommandLine": "npm test"}, "status": "RUNNING"}}) + "\n",
            json.dumps({"event": "step_update", "step_update": {"step_index": 2, "invoke_subagent": {"Role": "UI Auditor", "TypeName": "research"}, "status": "RUNNING"}}) + "\n",
            json.dumps({"event": "step_update", "step_update": {"step_index": 3, "browser_action": {"url": "https://example.com"}, "status": "DONE"}}) + "\n",
            json.dumps({"event": "tool_call", "tool_call": {"name": "edit_file", "arguments": {"Path": "src/App.tsx"}}}) + "\n",
            json.dumps({"event": "step_update", "step_update": {"step_index": 4, "step_type": "agent_response", "text_delta": "Streamed "}}) + "\n",
            json.dumps({"event": "result", "result": {"status": "SUCCESS", "conversation_id": "conv-xyz", "response": "Final Antigravity answer"}}) + "\n",
        ]

        class FakeProc:
            def __init__(self, lines):
                self.stdout = iter(lines)
                self.stderr = iter(())
                self._done = False

            def __enter__(self):
                return self

            def __exit__(self, *exc):
                return False

            def poll(self):
                return 0 if self._done else None

            def wait(self):
                self._done = True
                return 0

        def fake_spawn(args, **kwargs):
            captured_args.append(list(args))
            return FakeProc(list(stream_lines))

        write_updates = []
        with patch.object(providers, "executable", return_value="agy"), \
             patch.object(providers, "spawn", side_effect=fake_spawn), \
             patch.object(providers, "_find_agy_transcript", return_value=tpath):
            status, out = providers.agy_run(
                self.temp.name, "gemini-3-pro", "Inspect app", "write", threading.Event(),
                lambda detail, **kw: write_updates.append((detail, kw)),
            )
            self.assertEqual("finished", status)
            self.assertEqual("Final Antigravity answer", out)
            providers.agy_run(
                self.temp.name, "gemini-3-pro", "Read only check", "read", threading.Event(),
                lambda detail, **kw: None,
            )

        write_cmd, read_cmd = captured_args[0], captured_args[1]
        self.assertIn("--mode", write_cmd)
        self.assertEqual("auto", write_cmd[write_cmd.index("--mode") + 1])
        self.assertIn("--dangerously-skip-permissions", write_cmd)
        self.assertNotIn("--sandbox", write_cmd)
        self.assertIn("--print-timeout", write_cmd)
        self.assertEqual("0", write_cmd[write_cmd.index("--print-timeout") + 1])

        self.assertEqual("plan", read_cmd[read_cmd.index("--mode") + 1])
        self.assertIn("--sandbox", read_cmd)
        self.assertNotIn("--dangerously-skip-permissions", read_cmd)

        phases = {kw.get("phase") for _, kw in write_updates if kw.get("phase")}
        self.assertTrue({"command", "subagent", "browser", "file_edit", "reply", "finishing"}.issubset(phases))
        activities = [kw["activity_event"]["title"] for _, kw in write_updates if kw.get("activity_event")]
        self.assertIn("Background task update", activities)


if __name__ == "__main__":
    unittest.main()
