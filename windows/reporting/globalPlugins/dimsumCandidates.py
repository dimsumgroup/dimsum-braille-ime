import json
import os
from pathlib import Path
import re
import runpy
import time
from uuid import uuid4

import api
import config
import globalPluginHandler
import inputCore
from keyboardHandler import KeyboardInputGesture
from NVDAObjects.behaviors import CandidateItem
from NVDAObjects.inputComposition import InputComposition
import speech
from speech import extensions
import ui
import wx


BASE = Path(os.environ["LOCALAPPDATA"]) / "DimsumInput"
ASSETS = Path(__file__).resolve().parent.parent
FILES = ("characterDescriptions.dic", "symbols.dic", "cldr.dic")
DICTIONARY_DATA = runpy.run_path(str(ASSETS / "dictionaryData.py"))
CHARACTERS = DICTIONARY_DATA["CHARACTERS"]
SYMBOL_DICTIONARIES = DICTIONARY_DATA["SYMBOL_DICTIONARIES"]
CONFIG_SECTION = "dimsumPronunciations"
SCHEMAS = {"dimsum_cb", "dimsum_cb_std", "dimsum_full"}
DIRECT_MODES = {"standard", "compact"}
# Native candidate speech normally follows the Rime update immediately.
NATIVE_SPEECH_WINDOW_NS = 500_000_000


def load_pronunciations(directory, enabled=(CHARACTERS, *SYMBOL_DICTIONARIES)):
    pronunciations = {}
    if CHARACTERS in enabled:
        for line in (directory / FILES[0]).read_text(encoding="utf-8-sig").splitlines():
            if not line or line.startswith("#"):
                continue
            fields = line.split("\t")
            key = fields[0]
            if not DICTIONARY_DATA["is_described_character"](key) or key in pronunciations:
                continue
            groups = (group for field in fields[1:] for group in field.split(" "))
            groups = tuple(group.strip() for group in groups if group.strip() not in {"", "-"})
            if groups:
                pronunciations[key] = groups
    if any(name in enabled for name in SYMBOL_DICTIONARIES):
        for name, rows in DICTIONARY_DATA["split_symbols"](directory).items():
            if name in enabled:
                for key, (replacement, _) in rows.items():
                    if len(key) == 1 and replacement.strip() not in {"", "-"}:
                        pronunciations.setdefault(key, (replacement.strip(),))
    return pronunciations


def describe(text, pronunciations, all_groups=False):
    return "，".join(
        "，".join(groups if all_groups else groups[:1]) if groups else character
        for character in text if not character.isspace()
        for groups in (pronunciations.get(character),)
    )


def parse_report(data):
    fields = data.decode("utf-8").split("\0")
    if len(fields) < 7 or len(fields) % 2 == 0:
        return None
    protocol, session, revision, schema, event, selected, singleton, *items = fields
    if protocol != "dimsum-win-1" or schema not in SCHEMAS or not revision.isdecimal() or not selected.isdecimal():
        return None
    if event not in {"page", "browse", "tone", "same", "clear"}:
        return None
    return (session, int(revision)), schema, event, int(selected), singleton == "1", list(zip(items[::2], items[1::2]))


def native_candidate_speech(sequence, focus, items, selected):
    text = " ".join(part.strip() for part in sequence if isinstance(part, str)).rstrip(" ,，")
    if not text or not 0 < selected <= len(items):
        return False
    words = [word for _, word in items]
    names = [CandidateItem.getFormattedCandidateName(focus, number, word) for number, word in enumerate(words, 1)]
    if len(names) > 1 and text == ", ".join(names):
        return True
    word = words[selected - 1]
    if isinstance(focus, CandidateItem):
        name = names[selected - 1]
        if getattr(focus, "basicText", None) == word and (
            text in {word, name} or text.startswith((name + ", ", name + "，"))
        ):
            return True

    composition = focus if isinstance(focus, InputComposition) else getattr(focus, "parent", None)
    if not isinstance(composition, InputComposition) or composition.isReading:
        return False
    preedit = composition.compositionString.strip()
    if preedit == word:
        return text == word

    # Weasel's preview_all style appends a bracketed candidate page to the preedit.
    _, opening, preview = preedit.rpartition("  [")
    if not opening or not preview.endswith("]") or len(words) < 2:
        return False
    remainder = preview[:-1]
    for word in words:
        _, found, remainder = remainder.partition(word)
        if not found:
            return False
    return text in preview or text == preedit


def record_nvda_profile():
    """Share this NVDA process's current interface language and profile path."""
    try:
        import languageHandler
        from NVDAState import WritePaths

        profile = {
            "Language": languageHandler.getLanguage(),
            "ConfigDirectory": str(Path(WritePaths.configDir).resolve()),
        }
        BASE.mkdir(parents=True, exist_ok=True)
        marker = BASE / f"nvda-running-{os.getpid()}.json"
        temporary = marker.with_suffix(".tmp")
        temporary.write_text(json.dumps(profile), encoding="utf-8")
        temporary.replace(marker)
        return marker
    except (ImportError, AttributeError, OSError, TypeError, ValueError):
        return None


def copied_pronunciations(language=None):
    try:
        import languageHandler
        from NVDAState import WritePaths

        explicit = language is not None
        language = language if explicit else languageHandler.getLanguage()
        if language not in DICTIONARY_DATA["LOCALES"]:
            return None
        root = Path(WritePaths.configDir) / "DimsumPronunciations"
        for locale in (language,) if explicit or language == "zh" else (language, "zh"):
            directory = root / locale
            pointer = directory / "active.txt"
            revision = pointer.read_text(encoding="utf-8").strip() if pointer.is_file() else ""
            if re.fullmatch(r"[0-9a-f]{32}", revision):
                return directory / revision
    except (ImportError, AttributeError, OSError, TypeError, ValueError):
        pass
    return None


def enabled_pronunciations():
    speech_dictionaries = config.conf["speech"].get("symbolDictionaries", ())
    characters = config.conf[CONFIG_SECTION].get("useCharacterDescriptions", True)
    enabled = [CHARACTERS] if characters else []
    enabled.extend(name for name in SYMBOL_DICTIONARIES if name in speech_dictionaries)
    return tuple(enabled)


def initialize_symbol_defaults(prevConf=None):
    import characterProcessing

    section = config.conf[CONFIG_SECTION]
    profiles = getattr(config.conf, "profiles", ())
    profile_settings = profiles[-1].get(CONFIG_SECTION, {}) if profiles else section
    initialized = profile_settings.get("symbolsInitialized", False)
    if initialized is True or str(initialized).lower() == "true":
        return
    speech_settings = config.conf["speech"]
    if "symbolDictionaries" not in speech_settings:
        return
    selected = list(speech_settings["symbolDictionaries"])
    merged = list(dict.fromkeys((*selected, *SYMBOL_DICTIONARIES)))
    if merged != selected:
        speech_settings["symbolDictionaries"] = merged
        characterProcessing.clearSpeechSymbols()
    section["symbolsInitialized"] = True


def write_atomic(path, contents):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + "." + uuid4().hex + ".tmp")
    try:
        temporary.write_bytes(contents)
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def update_native_dictionaries():
    import characterProcessing
    from logHandler import log
    from NVDAState import WritePaths

    definitions = getattr(characterProcessing, "listAvailableSymbolDictionaryDefinitions", lambda: ())()
    if not set(SYMBOL_DICTIONARIES).issubset(definition.name for definition in definitions):
        return

    profile = Path(WritePaths.configDir)
    generated = {}
    changed = False
    for locale in DICTIONARY_DATA["LOCALES"]:
        try:
            source = copied_pronunciations(locale)
            if source is not None:
                if source not in generated:
                    generated[source] = DICTIONARY_DATA["native_symbol_files"](source)
                for name, text in generated[source].items():
                    target = ASSETS / "locale" / locale / f"symbols-{name}.dic"
                    contents = text.encode("utf-8")
                    if not target.is_file() or target.read_bytes() != contents:
                        write_atomic(target, contents)
                        changed = True
            user_symbols = profile / f"symbols-{locale}.dic"
            if not user_symbols.is_file() or user_symbols.is_symlink():
                continue
            original = user_symbols.read_bytes()
            cleaned = DICTIONARY_DATA["remove_legacy_symbol_block"](original)
            if cleaned != original:
                backup = profile / "DimsumPronunciations" / "symbol-migration-backups"
                write_atomic(backup / f"{locale}-{uuid4().hex}.dic", original)
                write_atomic(user_symbols, cleaned)
                changed = True
        except (OSError, UnicodeError):
            log.warning("Could not prepare a Dimsum symbol dictionary for %s", locale, exc_info=True)
    if changed:
        for definition in definitions:
            if definition.name in {"user", *SYMBOL_DICTIONARIES}:
                definition.symbols.invalidateAllData()
        characterProcessing.clearSpeechSymbols()


def register_pronunciation_settings():
    from gui import guiHelper, settingsDialogs

    class DimsumPronunciationPanel(settingsDialogs.SettingsPanel):
        title = "點心單字配詞"

        def makeSettings(self, settingsSizer):
            helper = guiHelper.BoxSizerHelper(self, sizer=settingsSizer)
            self.useCharacters = helper.addItem(wx.CheckBox(self, label="使用點心單字配詞"))
            self.useCharacters.SetValue(config.conf[CONFIG_SECTION]["useCharacterDescriptions"])

        def onSave(self):
            config.conf[CONFIG_SECTION]["useCharacterDescriptions"] = self.useCharacters.IsChecked()

    settingsDialogs.NVDASettingsDialog.categoryClasses.append(DimsumPronunciationPanel)
    return settingsDialogs.NVDASettingsDialog.categoryClasses, DimsumPronunciationPanel


class GlobalPlugin(globalPluginHandler.GlobalPlugin):
    def __init__(self):
        super().__init__()
        self.last_speech = ""
        self.own_speech = False
        self.speaking_directly = False
        self.direct_delivery = None
        self.recent_candidate_speech = []
        self.pronunciation_revision = None
        self.pronunciations = {}
        self.preferences = {}
        self.settings_stamp = None
        self.last_report = None
        self.latest_stamp = time.time_ns()
        self.delayed = None
        config.conf.spec[CONFIG_SECTION] = {
            "useCharacterDescriptions": "boolean(default=True)",
            "symbolsInitialized": "boolean(default=False)",
        }
        initialize_symbol_defaults()
        config.post_configProfileSwitch.register(initialize_symbol_defaults)
        self.settings_categories, self.settings_panel = register_pronunciation_settings()
        update_native_dictionaries()
        extensions.filter_speechSequence.register(self.filter_native_speech)
        extensions.pre_speech.register(self.capture)
        self.timer = wx.PyTimer(self.poll)
        self.timer.Start(75)
        self.profile_marker = record_nvda_profile()

    def terminate(self):
        self.timer.Stop()
        config.post_configProfileSwitch.unregister(initialize_symbol_defaults)
        self.settings_categories.remove(self.settings_panel)
        if self.delayed:
            self.delayed.Stop()
        extensions.filter_speechSequence.unregister(self.filter_native_speech)
        extensions.pre_speech.unregister(self.capture)
        self.last_speech = ""
        if self.profile_marker is not None:
            try:
                self.profile_marker.unlink(missing_ok=True)
            except OSError:
                pass
        super().terminate()

    def capture(self, speechSequence, originalSpeechSequence=None, **kwargs):
        if self.own_speech:
            return
        rendered = tuple(item.strip() for item in speechSequence if isinstance(item, str) and item.strip())
        original = speechSequence if originalSpeechSequence is None else originalSpeechSequence
        utterance = " ".join(item.strip() for item in original if isinstance(item, str) and item.strip())
        if utterance:
            self.last_speech = utterance
        if rendered:
            now = time.time_ns()
            handle = getattr(api.getFocusObject(), "windowHandle", None)
            self.recent_candidate_speech = self.recent_candidate_speech[-11:] + [(now, handle, rendered)]

    def say(self, text, *, lookup_reply=False):
        self.own_speech = lookup_reply
        try:
            ui.message(text)
        finally:
            self.own_speech = False

    def refresh_settings(self):
        settings = BASE / "settings.json"
        try:
            stamp = settings.stat().st_mtime_ns
        except FileNotFoundError:
            stamp = 0
        if stamp == self.settings_stamp:
            return
        preferences = json.loads(settings.read_text(encoding="utf-8")) if stamp else {}
        if preferences.get("ReportingMode") != self.preferences.get("ReportingMode"):
            self.direct_delivery = None
        self.preferences = preferences
        self.settings_stamp = stamp
        self.clearGestureBindings()
        shortcut = self.preferences.get("LookupGesture", "")
        if shortcut:
            try:
                for identifier in KeyboardInputGesture.fromName(shortcut.lower()).identifiers:
                    self.bindGesture(inputCore.normalizeGestureIdentifier(identifier), "lookup")
            except (LookupError, ValueError):
                pass

    def refresh_pronunciations(self):
        root = BASE / "pronunciations"
        pointer = root / "active.txt"
        revision = pointer.read_text(encoding="utf-8").strip() if pointer.is_file() else ""
        if not re.fullmatch("[0-9a-f]{40}(?:-[0-9a-f]{32})?", revision):
            revision = ""
        directory = root / revision if revision else copied_pronunciations() or ASSETS
        enabled = enabled_pronunciations()
        current = (directory, enabled)
        if current == self.pronunciation_revision:
            return
        try:
            self.pronunciations = load_pronunciations(directory, enabled)
        except (OSError, UnicodeError):
            if self.pronunciations and self.pronunciation_revision and self.pronunciation_revision[1] == enabled:
                return
            self.pronunciations = load_pronunciations(ASSETS, enabled)
        self.pronunciation_revision = current

    def script_lookup(self, gesture):
        if not enabled_pronunciations():
            self.say("目前未啟用點心配詞。", lookup_reply=True)
            return
        original = self.last_speech.strip()
        if not original:
            self.say("暫時未取得 NVDA 最近嘅報讀。", lookup_reply=True)
            return
        try:
            self.refresh_pronunciations()
            text = describe(original, self.pronunciations, all_groups=len(original) == 1)
            self.say(text or "呢段報讀冇可查詢嘅文字。", lookup_reply=True)
        except (OSError, UnicodeError):
            self.say("無法載入點心配詞。", lookup_reply=True)

    def read_report(self):
        found = []
        for slot in (1, 2):
            file = BASE / f"candidate-report-{slot}"
            try:
                with file.open("rb") as stream:
                    stat = os.fstat(stream.fileno())
                    if stat.st_size > 65536 or stat.st_mtime_ns < self.latest_stamp:
                        continue
                    report = parse_report(stream.read(65537))
                    if report:
                        found.append((stat.st_mtime_ns, report))
            except (OSError, UnicodeError, ValueError):
                continue
        return max(found, key=lambda entry: (entry[0], entry[1][0][1]), default=None)

    def latest_report(self):
        snapshot = self.read_report()
        if snapshot is None:
            return None
        stamp, latest = snapshot
        if self.last_report == latest[0]:
            return None
        self.latest_stamp = stamp
        self.last_report = latest[0]
        return latest

    def direct_candidate_text(self, report, mode, window):
        revision, schema, event, selected, _, items = report
        if event == "clear" or not items:
            self.direct_delivery = None
            return None
        key = (mode, window, revision)
        page = (mode, window, revision[0], schema, selected, items)
        if self.direct_delivery:
            last_key, last_page = self.direct_delivery
            if key == last_key or (event == "same" and page == last_page):
                return None

        use_descriptions = self.preferences.get("PronounceCandidates", True)
        if use_descriptions:
            try:
                self.refresh_pronunciations()
            except (OSError, UnicodeError):
                pass
        if event == "browse" and 0 < selected <= len(items):
            word = items[selected - 1][1]
            text = describe(word, self.pronunciations, all_groups=True) if use_descriptions else word
        else:
            spoken = []
            for label, candidate in items:
                word = describe(candidate, self.pronunciations) if use_descriptions else candidate
                spoken.append(f"{label} {word}" if mode == "standard" and schema != "dimsum_full" else word)
            text = "，".join(spoken)

        if not text:
            return None
        self.direct_delivery = (key, page)
        return text

    def filter_native_speech(self, value):
        if self.speaking_directly or self.own_speech:
            return value
        mode = self.preferences.get("ReportingMode")
        if mode not in DIRECT_MODES or speech.getState().speechMode != speech.SpeechMode.talk:
            return value
        snapshot = self.read_report()
        if snapshot is None:
            return value
        stamp, report = snapshot
        if not 0 <= time.time_ns() - stamp <= NATIVE_SPEECH_WINDOW_NS:
            return value
        _, _, event, selected, _, items = report
        focus = api.getFocusObject()
        handle = getattr(focus, "windowHandle", None)
        if handle is None or event == "clear" or not native_candidate_speech(value, focus, items, selected):
            return value
        if self.direct_delivery:
            last_key, _ = self.direct_delivery
            if last_key[2] == report[0] and last_key[1] != handle:
                return value
        text = self.direct_candidate_text(report, mode, handle)
        return [text] if text else []

    def poll(self):
        try:
            self.refresh_settings()
            mode = self.preferences.get("ReportingMode", "off")
            if mode == "off":
                self.latest_stamp = time.time_ns()
                return
            latest = self.latest_report()
            if latest is None:
                return
            if self.delayed:
                self.delayed.Stop()
                self.delayed = None
            _, _, event, selected, _, items = latest
            focus = api.getFocusObject()
            handle = getattr(focus, "windowHandle", None)
            if mode == "tone":
                if items and event == "tone":
                    self.delayed = wx.CallLater(120, self.supplement, items, selected, handle, self.latest_stamp)
                return
            if mode in DIRECT_MODES:
                if self.direct_delivery and self.direct_delivery[0][2] == latest[0]:
                    return
                text = self.direct_candidate_text(latest, mode, handle)
                if text:
                    self.speaking_directly = True
                    try:
                        ui.message(text, speechPriority=speech.Spri.NEXT)
                    finally:
                        self.speaking_directly = False
        except (OSError, UnicodeError, ValueError):
            return

    def supplement(self, items, selected, window_handle, reported_at):
        focus = api.getFocusObject()
        if self.preferences.get("ReportingMode") != "tone":
            return
        if window_handle is None or getattr(focus, "windowHandle", None) != window_handle:
            return
        if not 0 < selected <= len(items):
            return
        settings = config.conf["inputComposition"]
        report_page = settings["autoReportAllCandidates"]
        if not report_page and not settings["announceSelectedCandidate"]:
            return
        label, candidate = items[selected - 1]
        format_name = getattr(focus, "getFormattedCandidateName", None)
        names = [f"{key} {word}" for key, word in items]
        nvda_names = [format_name(index, word) for index, (_, word) in enumerate(items, 1)] if callable(format_name) else names
        for timestamp, handle, utterance in self.recent_candidate_speech:
            if timestamp < reported_at or handle != window_handle:
                continue
            spoken = " ".join(utterance).rstrip(" ,，")
            if report_page and len(items) > 1:
                if spoken in {", ".join(nvda_names), "，".join(names), " ".join(nvda_names)}:
                    return
                continue
            native_name = nvda_names[selected - 1]
            if (spoken in {candidate, names[selected - 1], native_name, f"{label}, {candidate}", f"{label}，{candidate}"}
                    or spoken.startswith((native_name + ", ", native_name + "，"))):
                return
        if self.preferences.get("PronounceCandidates", True):
            try:
                self.refresh_pronunciations()
                candidate = describe(candidate, self.pronunciations)
            except (OSError, UnicodeError):
                pass
        if report_page:
            words = names
            if self.preferences.get("PronounceCandidates", True):
                words = [f"{key} {describe(text, self.pronunciations)}" for key, text in items]
            self.say("，".join(words))
        elif settings["announceSelectedCandidate"]:
            self.say(candidate)
