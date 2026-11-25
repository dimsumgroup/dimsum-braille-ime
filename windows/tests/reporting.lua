-- Run from the windows directory. Use real Lua with a small Rime/Windows file mock.
local writer
local files = {}
local published
local reporting_enabled, sound_enabled = true, false
os.getenv = function() return "mock" end
local prefix = "mock\\DimsumInput\\"
io.open = function(path, mode)
    if mode == "rb" and ((reporting_enabled and path == prefix .. "candidate-reporting-enabled")
        or (sound_enabled and path == prefix .. "singleton-sound-enabled")) then
        return {close = function() return true end}
    end
    if mode ~= "wb" then return nil end
    return {
        write = function(_, value) writer = value; return true end,
        close = function() files[path] = writer; return true end,
    }
end
os.remove = function(path) files[path] = nil end
os.rename = function(from, to)
    assert(not files[to], "Windows rename must use a free destination")
    files[to], files[from] = files[from], nil
    published = {}
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
for _, field in ipairs(published) do assert(field ~= "bp", "Raw input must not be written") end
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
assert(published[9] == "日本" and published[7] == "0", "Last page with one of several candidates")
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
print("Lua reporting passed")
