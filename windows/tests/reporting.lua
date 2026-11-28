-- Run from the windows directory. Use real Lua with a small Rime/Windows file mock.
local writer
local files = {}
local published, published_path
local blocked = {}
local reporting_enabled, sound_enabled, external_enabled = true, false, false
os.getenv = function() return "mock" end
local prefix = "mock\\DimsumInput\\"
io.open = function(path, mode)
    if mode == "rb" and ((reporting_enabled and path == prefix .. "candidate-reporting-enabled")
        or (external_enabled and path == prefix .. "external-candidate-reporting-enabled")
        or (sound_enabled and path == prefix .. "singleton-sound-enabled")) then
        return {close = function() return true end}
    end
    if mode ~= "wb" then return nil end
    return {
        write = function(_, value) writer = value; return true end,
        close = function() files[path] = writer; return true end,
    }
end
os.remove = function(path)
    if blocked[path] then return nil, "sharing violation" end
    files[path] = nil
    return true
end
os.rename = function(from, to)
    if blocked[to] then return nil, "sharing violation" end
    assert(not files[to], "Windows rename must use a free destination")
    files[to], files[from] = files[from], nil
    published, published_path = {}, to
    for field in (files[to] .. "\0"):gmatch("([^%z]*)%z") do
        published[#published + 1] = field
    end
    return true
end

local function notifier()
    return {
        connect = function(self, callback)
            self.emit = callback
            return {disconnect = function() self.emit = nil end}
        end,
    }
end
local candidates = {"不過", "不久"}
local segment = {
    start = 0, selected_index = 0,
    get_candidate_at = function(_, index) return {text = candidates[index + 1]} end,
    menu = {prepare = function(_, count) return math.min(count, #candidates) end},
}
local context = {
    input = "bp",
    get_preedit = function(self) return {text = self.input} end,
    get_option = function() return false end,
    composition = {empty = function() return #candidates == 0 end, back = function() return segment end},
    update_notifier = notifier(),
    select_notifier = notifier(),
}
local env = {
    engine = {
        context = context,
        schema = {
            schema_id = "dimsum_cb",
            page_size = 2,
            config = {get_string = function() return "ab" end, get_list = function() return nil end},
        },
    },
}
local reporter = dofile("reporting/rime/lua/dimsum_windows_reporter.lua")
reporter.init(env)
context.update_notifier.emit(context)
assert(published[5] == "page" and published[8] == "a" and published[9] == "不過")
for _, field in ipairs(published) do
    assert(not field:find("bp", 1, true), "Raw input must not be written")
end
assert(published[2]:match("/p2:00597757$"), "Publish a signature of the private preedit")
context.input = "bpA"
context.update_notifier.emit(context)
assert(published[5] == "tone")
local revision = published[3]
context.select_notifier.emit(context)
assert(published[3] == revision, "The same state must not publish twice")
segment.selected_index = 1
context.update_notifier.emit(context)
assert(published[5] == "browse" and published[6] == "2")
candidates = {"不久", "不過", "日本"}
context.update_notifier.emit(context)
assert(published[9] == "不久" and published[11] == "不過")
segment.selected_index = 2
context.update_notifier.emit(context)
assert(published[5] == "browse" and published[9] == "日本" and published[7] == "0",
    "An adjacent candidate on the last page should be browsed without the singleton sound")
segment.selected_index = 1
candidates = {"不便", "不過", "日本"}
context.update_notifier.emit(context)
assert(published[5] == "browse" and published[6] == "2", "Adjacent movement still works when the page is rebuilt")
context.has_menu = function() return #candidates > 0 end
local key = {repr = function() return "Down" end}
env.engine.process_key = function(_, event)
    assert(reporter.func(event, env) == 2, "The nested key event uses normal Rime processors")
    segment.selected_index = 2
    return true
end
assert(reporter.func(key, env) == 1 and published[5] == "browse" and published[9] == "日本",
    "Report the native selection even if Rime sends no update notification")
env.engine.process_key = function(_, event)
    assert(reporter.func(event, env) == 2)
    segment.selected_index = 1
    context.update_notifier.emit(context)
    return true
end
revision = tonumber(published[3])
assert(reporter.func(key, env) == 1 and tonumber(published[3]) == revision + 1,
    "A notified move and the navigation fallback must report once")
local function page_key(rep, to, notify)
    local event = {repr = function() return rep end}
    env.engine.process_key = function(_, key_event)
        assert(reporter.func(key_event, env) == 2)
        segment.selected_index = to
        if notify then context.update_notifier.emit(context) end
        return true
    end
    assert(reporter.func(event, env) == 1)
end

-- A one-item final page can change the absolute index by only one.
segment.selected_index = 1
page_key("Next", 2, true)
assert(published[5] == "page" and published[9] == "日本" and published[7] == "0")
page_key("Page_Up", 1, false)
assert(published[5] == "page" and published[9] == "不便" and published[11] == "不過")
revision = published[3]
page_key("KP_Page_Up", 1, false)
assert(published[3] == revision, "An unchanged boundary must not report again")

local active = tonumber(published_path:sub(-1))
local locked = prefix .. "candidate-report-" .. tostring(active % 3 + 1)
files[locked] = files[published_path]
blocked[locked] = true
page_key("KP_Next", 2, false)
assert(published_path ~= locked and tonumber(published[3]) == tonumber(revision) + 1 and published[9] == "日本",
    "Use an alternate slot when a reader holds the next report file open")
blocked[locked] = nil

candidates = {"你"}
segment.start, segment.selected_index = 3, 0
context.select_notifier.emit(context)
assert(published[5] == "page" and published[9] == "你" and published[7] == "1")
candidates = {}
context.update_notifier.emit(context)
assert(published[5] == "clear" and #published == 7)

segment.start, segment.selected_index = 0, 0
context.input, candidates = "f$", {"分", "芬"}
context.update_notifier.emit(context)
context.input, candidates = "f$A", {"分", "份"}
context.update_notifier.emit(context)
assert(published[5] == "tone" and published[9] == "分" and published[11] == "份")
revision = published[3]
context.select_notifier.emit(context)
assert(published[3] == revision)

context.input, candidates = "f$", {"分", "芬"}
context.update_notifier.emit(context)
context.input, candidates = "f$B", {"粉", "份"}
context.update_notifier.emit(context)
assert(published[5] == "page", "A different highlighted candidate should use normal reporting")
reporter.fini(env)
assert(not next(files))

reporting_enabled, sound_enabled = false, true
local sound_path = prefix .. "singleton-sound-event"
reporter.init(env)
segment.start, segment.selected_index = 0, 2
context.input, candidates = "yu", {"語", "魚", "雨"}
context.update_notifier.emit(context)
assert(files[sound_path] == nil, "The last page of a multi-page list should not sound.")
segment.selected_index = 0
candidates = {"魚"}
context.update_notifier.emit(context)
local signal = files[sound_path]
assert(signal and not signal:find("魚"))
assert(not files[prefix .. "candidate-report-1"] and not files[prefix .. "candidate-report-2"])
context.input = "yuA"
context.update_notifier.emit(context)
assert(files[sound_path] == signal, "An unchanged single candidate should sound once.")
candidates = {}
context.update_notifier.emit(context)
candidates = {"魚"}
context.update_notifier.emit(context)
assert(files[sound_path] ~= signal, "A new composition may offer the same candidate again.")
reporter.fini(env)
reporting_enabled, sound_enabled, external_enabled = true, false, false
reporter.init(env)
candidates = {"過", "日本", "天氣"}
context.update_notifier.emit(context)
assert(published_path == prefix .. "candidate-report-1")
reporting_enabled, external_enabled = false, true
context.update_notifier.emit(context)
assert(published_path:find("external-candidate-report-", 1, true) and published[9] == "過")
assert(not files[prefix .. "candidate-report-1"], "Old NVDA add-ons may only see the NVDA channel.")
page_key("Next", 2, false)
assert(published[5] == "page" and published[9] == "天氣")
reporting_enabled, external_enabled = true, false
context.update_notifier.emit(context)
assert(published_path:find(prefix .. "candidate-report-", 1, true))
assert(not files[prefix .. "external-candidate-report-1"] and not files[prefix .. "external-candidate-report-2"])
reporter.fini(env)
print("Lua reporting passed")
