-- Publish the current candidate page for the Dimsum Voice Helper.
-- The processor observes Rime and leaves each key to the rest of the engine.
local reporter = {}
local revision = 0

local function publish(context, env, event)
    revision = revision + 1
    local fields = { "dimsum-2", env.engine.schema.schema_id, context.input, "0", "0", "0",
                     event, env.session .. ":" .. tostring(revision) }
    local composition = context.composition
    if not composition:empty() then
        local segment = composition:back()
        local menu = segment.menu
        fields[4] = tostring(segment.start)
        if menu then
            local size = math.min(9, math.max(1, env.engine.schema.page_size))
            local first = math.floor(segment.selected_index / size) * size
            local total = menu:prepare(first + size + 1)
            if total > first then
                fields[5] = tostring(segment.selected_index - first + 1)
            end
            fields[6] = first == 0 and total == 1 and "1" or "0"
            for index = first, math.min(total, first + size) - 1 do
                local candidate = segment:get_candidate_at(index)
                if candidate then
                    fields[#fields + 1] = candidate.text
                end
            end
        end
    end

    local temporary = env.report_path .. ".tmp"
    local file = io.open(temporary, "wb")
    if not file then return end
    local written = file:write(table.concat(fields, "\0"))
    if file:close() and written then
        os.rename(temporary, env.report_path)
    end
end

function reporter.init(env)
    env.session = tostring(env) .. ":" .. tostring(os.time())
    env.report_path = rime_api.get_user_data_dir() .. "/dimsum-reporting/candidates"
    local context = env.engine.context
    env.updated = context.update_notifier:connect(function(ctx) publish(ctx, env, "update") end)
    env.selected = context.select_notifier:connect(function(ctx) publish(ctx, env, "selection") end)
end

function reporter.fini(env)
    env.updated:disconnect()
    env.selected:disconnect()
end

function reporter.func()
    return 2 -- kNoop
end

return reporter
