-- Derived from the Dimsum macOS candidate reporter. Never writes raw key codes.
local reporter = {}
local report_slots = 3
local revision = 0
local active_slot
local active_engine
local sound_revision = 0

local function enabled(path)
    local flag = io.open(path, "rb")
    if not flag then return false end
    flag:close()
    return true
end

local function publish_sound(env, current)
    -- This marker contains only a sequence number. No candidate text leaves Rime for audio.
    if not current.singleton or not current.fields[2] then
        env.last_sound = nil
        return
    end
    local candidate = tostring(current.start) .. "\0" .. current.fields[2]
    if env.last_sound == candidate then return end
    local file = io.open(env.sound_event, "wb")
    if not file then return end
    sound_revision = sound_revision + 1
    local written = file:write(env.session .. ":" .. sound_revision .. "\n")
    if file:close() and written then env.last_sound = candidate end
end

local function same_selected_candidate(previous, current)
    local index = current.selected * 2
    return current.selected > 0 and previous.selected == current.selected
        and previous.start == current.start and previous.page == current.page
        and current.fields[index] ~= nil and previous.fields[index] == current.fields[index]
        and previous.fields[index - 1] == current.fields[index - 1]
end

local function page_snapshot(context, env)
    local composition = context.composition
    local fields = {}
    local start, selected, count, first = 0, 0, 0, 0
    local singleton = false
    if not composition:empty() and not context:get_option("_hide_candidate") then
        local segment = composition:back()
        local menu = segment.menu
        start = segment.start
        if menu then
            local size = math.max(1, math.min(32, env.engine.schema.page_size))
            first = math.floor(segment.selected_index / size) * size
            count = menu:prepare(first + size + 1)
            selected = count > first and segment.selected_index - first + 1 or 0
            singleton = first == 0 and count == 1
            local config = env.engine.schema.config
            local keys = config:get_string("menu/alternative_select_keys") or "1234567890"
            local labels = config:get_list("menu/alternative_select_labels")
            if labels and labels.size < size then labels = nil end
            for index = first, math.min(count, first + size) - 1 do
                local candidate = segment:get_candidate_at(index)
                if candidate then
                    local offset = index - first
                    local label = labels and labels:get_value_at(offset)
                    local key = label and label.value or keys:sub(offset + 1, offset + 1)
                    fields[#fields + 1] = key ~= "" and key or tostring(offset + 1)
                    fields[#fields + 1] = candidate.text
                end
            end
        end
    end
    return {
        raw = context.input, start = start, page = first, selected = selected,
        singleton = singleton, fields = fields, candidates = table.concat(fields, "\0")
    }
end

local function write_snapshot(path, payload)
    -- Never replace the last complete report. If a reader has another slot
    -- open without delete sharing, publish through the remaining slot.
    for offset = 1, report_slots do
        local slot = ((active_slot or 0) + offset - 1) % report_slots + 1
        if slot ~= active_slot then
            local target = path .. tostring(slot)
            local temporary = target .. ".tmp"
            local file = io.open(temporary, "wb")
            if file then
                local written = file:write(payload)
                if file:close() and written then
                    os.remove(target)
                    if os.rename(temporary, target) then
                        for previous = 1, report_slots do
                            if previous ~= slot then os.remove(path .. tostring(previous)) end
                        end
                        return slot
                    end
                end
                os.remove(temporary)
            end
        end
    end
end

local function publish(context, env, navigation)
    if not env.path then return end
    local report = enabled(env.flag)
    local sound = enabled(env.sound_flag)
    if not report and not sound then
        env.previous = nil
        env.last_sound = nil
        return
    end
    local current = page_snapshot(context, env)
    if sound then publish_sound(env, current) else env.last_sound = nil end
    if not report then
        env.previous = nil
        return
    end
    local previous = active_engine == env.session and env.previous

    local same_candidates = previous and previous.candidates == current.candidates
        and previous.start == current.start and previous.page == current.page
        and previous.singleton == current.singleton
    local same_input = previous and previous.raw == current.raw
    local same_selection = previous and previous.selected == current.selected
    if same_candidates and same_input and same_selection then return end

    local event = "page"
    if #current.fields == 0 then
        event = "clear"
    elseif previous and previous.raw ~= "" and current.raw:sub(1, #previous.raw) == previous.raw
        and current.raw:sub(#previous.raw + 1):match(env.tone_pattern)
        and same_selected_candidate(previous, current) then
        event = "tone"
    elseif navigation ~= "page" and same_input and previous.start == current.start and previous.selected > 0 and current.selected > 0
        and math.abs(previous.page + previous.selected - current.page - current.selected) == 1 then
        event = "browse"
    elseif navigation ~= "page" and same_candidates and same_selection and previous.raw ~= "" and current.raw ~= previous.raw then
        event = "same"
    end

    revision = revision + 1
    local fields = {
        "dimsum-win-1", env.session, tostring(revision), env.engine.schema.schema_id,
        event, tostring(current.selected), current.singleton and "1" or "0"
    }
    for _, value in ipairs(current.fields) do fields[#fields + 1] = value end
    local slot = write_snapshot(env.path, table.concat(fields, "\0"))
    if slot then
        active_slot = slot
        active_engine = env.session
        env.previous = current
    end
end

function reporter.init(env)
    env.session = tostring(env) .. ":" .. os.time()
    env.tone_pattern = env.engine.schema.schema_id == "dimsum_full" and "^[1-6]$" or "^[A-F]$"
    local local_data = os.getenv("LOCALAPPDATA")
    if not local_data then return end
    env.path = local_data .. "\\DimsumInput\\candidate-report-"
    env.flag = local_data .. "\\DimsumInput\\candidate-reporting-enabled"
    env.sound_flag = local_data .. "\\DimsumInput\\singleton-sound-enabled"
    env.sound_event = local_data .. "\\DimsumInput\\singleton-sound-event"
    local context = env.engine.context
    local function on_change(ctx)
        if not env.navigating then publish(ctx, env) end
    end
    env.update = context.update_notifier:connect(on_change)
    env.selection = context.select_notifier:connect(on_change)
end

function reporter.fini(env)
    if env.update then env.update:disconnect() end
    if env.selection then env.selection:disconnect() end
    if env.path and active_engine == env.session then
        for slot = 1, report_slots do
            os.remove(env.path .. tostring(slot))
            os.remove(env.path .. tostring(slot) .. ".tmp")
        end
        active_slot, active_engine = nil, nil
    end
end

local navigation_keys = {
    Up = "browse", Down = "browse", KP_Up = "browse", KP_Down = "browse",
    Prior = "page", Next = "page", Page_Up = "page", Page_Down = "page",
    KP_Prior = "page", KP_Next = "page", KP_Page_Up = "page", KP_Page_Down = "page"
}

function reporter.func(key, env)
    if env.navigating or not env.flag then return 2 end
    local navigation = navigation_keys[key:repr()]
    if not navigation then return 2 end
    local context = env.engine.context
    if not context:has_menu() or not enabled(env.flag) then return 2 end

    -- Let the normal key chain change the highlight or page, including
    -- arrows sent by G/H bindings. Publish once with the actual result.
    env.navigating = true
    local ok, handled = pcall(env.engine.process_key, env.engine, key)
    env.navigating = false
    if not ok or not handled then return 2 end
    publish(context, env, navigation)
    return 1 -- kAccepted
end

return reporter
