-- description: sveglia: an alarm app: pick hours and minutes, many daily alarms, its own ring pattern, an OK to stop
-- Kind: app (fixed by the project). The app reloads this file within a second of a save.
-- Skill: quest-plugins.

local MAX_ALARMS = 20

local S = { next_n = 1, alarms = {}, h12 = true }   -- alarms: { {n, h, m, on} }; h12: show am / pm
local ring_ids = {}                     -- ring timers in flight
local armed_ids = {}                    -- scheduled alarm timers, per alarm n
local delete_mode = false
local P = { h = 7, m = 30 }             -- the picker (session only)

local function save() store.set("state", json.encode(S)) end
local function load()
  local t = json.decode(store.get("state") or "")
  if type(t) == "table" then
    if type(t.alarms) == "table" then
      local keep = {}
      for _, a in ipairs(t.alarms) do
        if type(a) == "table" and type(a.h) == "number" and type(a.m) == "number" and type(a.n) == "number" then
          keep[#keep + 1] = { n = a.n, h = a.h, m = a.m, on = a.on ~= false }
        end
      end
      t.alarms = keep
    end
    for k, v in pairs(t) do S[k] = v end
    if type(S.alarms) ~= "table" then S.alarms = {} end
  end
end

local function pad2(x) return string.format("%02d", x) end

-- display a time: 12-hour with AM / PM (S.h12) or 24-hour
local function label(a)
  if S.h12 then
    local h = a.h % 12; if h == 0 then h = 12 end
    return h .. ":" .. pad2(a.m) .. (a.h >= 12 and " PM" or " AM")
  end
  return pad2(a.h) .. ":" .. pad2(a.m)
end

local function occurrence(a, day_table)
  return app.clock{ year = day_table.year, month = day_table.month, day = day_table.day,
                    hour = a.h, min = a.m, sec = 0 }
end

local function next_occurrence(a)
  local d = app.date()
  local t = occurrence(a, d)
  if t <= app.clock() then
    local nd = app.date(t + 24 * 60 * 60)   -- tomorrow, at this clock time
    t = app.clock{ year = nd.year, month = nd.month, day = nd.day, hour = a.h, min = a.m, sec = 0 }
  end
  return t
end

local render  -- below

-- the ring: my own sound, a composed pattern of built-in tones:
-- three sharp "alert" beeps, a pause, then a rising "ping ping alert", repeated until stopped
local function stop_ring()
  for _, id in ipairs(ring_ids) do app.cancel(id) end
  ring_ids = {}
end

local function ring_once()
  ui.sound("alert")
  ring_ids[#ring_ids + 1] = app.after(0.45, function() ui.sound("alert") end)
  ring_ids[#ring_ids + 1] = app.after(0.90, function() ui.sound("alert") end)
  ring_ids[#ring_ids + 1] = app.after(1.60, function() ui.sound("ping") end)
  ring_ids[#ring_ids + 1] = app.after(1.90, function() ui.sound("ping") end)
  ring_ids[#ring_ids + 1] = app.after(2.20, function() ui.sound("alert") end)
  ring_ids[#ring_ids + 1] = app.after(3.20, function() ring_once() end)
end

local ringing = false

local function ring_for(a)
  ringing = true
  ring_once()
  local hour12 = (a.h % 12 == 0) and 12 or (a.h % 12)
  ui.notify{ text = "ALARM " .. label(a), tone = "warn", sound = false,
             speak = "Alarm. It is " .. hour12 .. " o'clock.", panel = "main" }
end

local function on_fired(n)
  local a
  for _, x in ipairs(S.alarms) do if x.n == n then a = x end end
  if not a or not a.on then return end
  armed_ids[n] = app.at(next_occurrence(a), function() on_fired(n) end)   -- re-arm for the next day
  ring_for(a)
  ui.modal("ALARM " .. label(a), "The alarm is ringing.", { "OKAY" }, function(i)
    stop_ring(); ringing = false; render()
  end)
end

local function arm()
  for _, id in pairs(armed_ids) do app.cancel(id) end
  armed_ids = {}
  local missed = {}
  local d = app.date()
  local now = app.clock()
  for _, a in ipairs(S.alarms) do
    if a.on then
      local today_t = occurrence(a, d)
      if now >= today_t and now - today_t < 90 then missed[#missed + 1] = label(a) end
      armed_ids[a.n] = app.at(next_occurrence(a), function() on_fired(a.n) end)
    end
  end
  if #missed > 0 then
    ui.notify{ text = "Alarm rang while the app was closed: " .. table.concat(missed, ", "), tone = "warn" }
  end
end

local function find(n)
  for i, a in ipairs(S.alarms) do if a.n == n then return i, a end end
end

local function sort()
  table.sort(S.alarms, function(x, y)
    if x.h ~= y.h then return x.h < y.h end
    if x.m ~= y.m then return x.m < y.m end
    return x.n < y.n
  end)
end

local function add(h, m)
  if h < 0 or h > 23 or m < 0 or m > 59 then return false, "use a time from 00:00 to 23:59" end
  if #S.alarms >= MAX_ALARMS then return false, MAX_ALARMS .. " alarms at most" end
  for _, a in ipairs(S.alarms) do
    if a.h == h and a.m == m then return false, label{ h = h, m = m } .. " is already set" end
  end
  local n = S.next_n; S.next_n = S.next_n + 1
  S.alarms[#S.alarms + 1] = { n = n, h = h, m = m, on = true }
  sort(); save(); arm()
  return true
end

local function delete(n)
  local i = find(n)
  if not i then return end
  if armed_ids[n] then app.cancel(armed_ids[n]); armed_ids[n] = nil end
  table.remove(S.alarms, i); save(); arm()
end

local function toggle(n)
  local _, a = find(n)
  if not a then return end
  a.on = not a.on; save(); arm()
end

local function parse_time(text)
  local h, m = (text or ""):match("(%d+)%D+(%d+)")
  if not h then h, m = (text or ""):match("^(%d%d?)(%d%d)$") end
  if not h then return nil end
  return tonumber(h), tonumber(m)
end

local WORDS = {
  oh = 0, zero = 0, one = 1, two = 2, three = 3, four = 4, five = 5, six = 6, seven = 7,
  eight = 8, nine = 9, ten = 10, eleven = 11, twelve = 12, thirteen = 13, fourteen = 14,
  fifteen = 15, sixteen = 16, seventeen = 17, eighteen = 18, nineteen = 19, twenty = 20,
  thirty = 30, forty = 40, fourty = 40, fifty = 50, ["half"] = 30,
}

-- "seven thirty" -> 7, 30; "seven" or "oh seven" -> 7, 0; "ten forty five" -> 10, 45
local function parse_words(text)
  local nums = {}
  local pending_hour = nil
  for w in (text or ""):gmatch("%a+") do
    w = w:lower()
    if w == "oh" or w == "zero" then
      pending_hour = 0
    else
      local v = WORDS[w]
      if v then
        if pending_hour ~= nil then v = pending_hour * 10 + v end   -- "oh seven" -> 7
        pending_hour = nil
        local last = nums[#nums]
        if v < 10 and last and last >= 20 and last < 60 and last % 10 == 0 then
          nums[#nums] = last + v          -- "forty five" -> 45
        else
          nums[#nums + 1] = v
        end
      elseif w == "oclock" or w == "clock" then
        -- ignore
      end
    end
  end
  if pending_hour ~= nil then nums[#nums + 1] = pending_hour end
  if #nums == 0 then return nil end
  if #nums == 1 then return nums[1], 0 end
  local h, m = nums[1], nums[2]
  if h > 23 and m <= 23 then h, m = m, h end   -- "thirty seven"? keep the sane order
  return h, m
end

local function parse_any(text)
  local h, m = parse_time(text)
  if not h then h, m = parse_words(text) end
  if not h then return nil end
  text = (text or ""):lower()
  if text:match("p[%.%s]*m[%.%s]*$") or text:match("evening") or text:match("afternoon") then
    if h < 12 then h = h + 12 end
  elseif text:match("a[%.%s]*m[%.%s]*$") or text:match("morning") then
    if h == 12 then h = 0 end
  end
  return h, m
end

local function next_label()
  local best, best_t = nil, math.huge
  local now = app.clock()
  for _, a in ipairs(S.alarms) do
    if a.on then
      local t = next_occurrence(a)
      if t < best_t then best, best_t = a, t end
    end
  end
  if not best then return "none" end
  local left = math.floor(best_t - now)
  if left < 90 then return label(best) .. " (-" .. left .. "s)" end
  return label(best) .. string.format(" (-%dh %02dm)", math.floor(left / 3600), math.floor((left % 3600) / 60))
end

render = function()
  local d = app.date()
  local cells, shown = {}, 0
  for _, a in ipairs(S.alarms) do
    shown = shown + 1
    if shown <= 6 then
      cells[#cells + 1] = { id = "alarm-" .. a.n, text = label(a) .. (a.on and "" or " OFF"),
                            sub = delete_mode and "tap = delete" or (a.on and "tap = off" or "tap = on"),
                            tone = delete_mode and "warn" or (a.on and "accent" or "dim") }
    end
  end
  if shown > 6 then cells[#cells + 1] = { text = "+" .. (shown - 6) .. " more", tone = "dim" } end

  -- the picker: a wheel in two columns (hours on the left, minutes on the right):
  -- tap an arrow to spin, tap a value to pick it
  P.m = P.m - (P.m % 5)
  local function htext(h)
    if S.h12 then
      local x = h % 12; if x == 0 then x = 12 end
      return x .. (h >= 12 and " PM" or " AM")
    end
    return pad2(h)
  end
  local pick = {}
  pick[#pick + 1] = { id = "hup", text = "↑", tone = "dim" }
  pick[#pick + 1] = { id = "mup", text = "↑", tone = "dim" }
  for off = -2, 2 do
    local h = (P.h + off) % 24
    local m = (P.m + off * 5) % 60
    if off == 0 then
      pick[#pick + 1] = { id = "ph-" .. h, text = htext(h), tone = "accent" }
      pick[#pick + 1] = { id = "pm-" .. m, text = pad2(m), tone = "accent" }
    else
      local tone = math.abs(off) == 1 and nil or "dim"
      pick[#pick + 1] = { id = "ph-" .. h, text = htext(h), tone = tone }
      pick[#pick + 1] = { id = "pm-" .. m, text = pad2(m), tone = tone }
    end
  end
  pick[#pick + 1] = { id = "hdn", text = "↓", tone = "dim" }
  pick[#pick + 1] = { id = "mdn", text = "↓", tone = "dim" }

  local comps = {
    { type = "text", id = "clock", style = "big", text = label{ h = d.hour, m = d.min } },
    { type = "text", id = "next", tone = "dim", text = "next: " .. next_label() },
  }
  if #cells > 0 then comps[#comps + 1] = { type = "grid", columns = 1, title = "alarms", cells = cells } end
  comps[#comps + 1] = { type = "text", id = "picker", style = "big", text = "pick: " .. label(P) }
  comps[#comps + 1] = { type = "grid", columns = 2, title = "hour | minute", cells = pick }
  local items
  if ringing then
    items = { { id = "okay", label = "OKAY STOP", tone = "error" },
              { id = "add", label = "ADD ALARM", tone = "accent" },
              { id = "ring", label = "RING TEST" } }
  elseif delete_mode then
    items = { { id = "delmode", label = "DONE" },
              { id = "clear", label = "CLEAR ALL", tone = "error" },
              { id = "ring", label = "RING TEST" } }
  else
    items = { { id = "add", label = "ADD ALARM", tone = "accent" },
              { id = "delmode", label = "DELETE" },
              { id = "ring", label = "RING TEST" } }
  end
  comps[#comps + 1] = { type = "buttons", items = items }

  ui.panel{ id = "main", title = "Sveglia", components = comps, on = function(e)
    if e.type == "button" then
      if e.id == "add" then
        local ok, err = add(P.h, P.m)
        if not ok then ui.toast(err or "could not add", "warn") end
      elseif e.id == "ring" then
        ring_for{ h = P.h, m = P.m }
        ui.modal("Test ring", "Ringing the pattern now.", { "OKAY" }, function() stop_ring(); ringing = false; render() end)
      elseif e.id == "delmode" then
        delete_mode = not delete_mode
      elseif e.id == "clear" then
        S.alarms = {}; save(); arm(); delete_mode = false
      elseif e.id == "okay" then
        stop_ring(); ringing = false
      end
      render()
    elseif e.type == "toggle" and e.id == "h12" then
      S.h12 = e.value; save(); render()
    elseif e.type == "cell" then
      local id = tostring(e.id)
      local n = tonumber(id:match("^alarm%-([%d]+)$"))
      if n then
        if delete_mode then delete(n) else toggle(n) end
        render()
        return
      end
      if id == "hup" then P.h = (P.h + 1) % 24; render(); return end
      if id == "hdn" then P.h = (P.h + 23) % 24; render(); return end
      if id == "mup" then P.m = (P.m + 5) % 60; render(); return end
      if id == "mdn" then P.m = (P.m + 55) % 60; render(); return end
      local h = tonumber(id:match("^ph%-([%d]+)$"))
      if h and h >= 0 and h <= 23 then P.h = h; render(); return end
      local m = tonumber(id:match("^pm%-([%d]+)$"))
      if m and m >= 0 and m <= 55 and m % 5 == 0 then P.m = m; render() end
    end
  end }
end

app.every(1, function()
  -- only the LIVE texts (clock, next) change each second; the rest of the spec stays identical
  render()
end)

load(); render(); arm()
menu.add{ id = "main", label = "SVEGLIA", glyph = "◆", icon = "bell", panel = "main" }

voice.command{
  name = "sveglia_add",
  description = "Add a daily alarm in the Sveglia app (time: hour and minute, e.g. 7:30)",
  params = { time = { type = "string", description = "the time, e.g. 7:30", required = true } },
  phrases = { "add [an] alarm [at|for] {time}", "wake me up at {time}", "set [an] alarm [for|at] {time}" },
  samples = { "Add an alarm at seven thirty", "Wake me up at 06:15", "Set an alarm for nine o'clock",
              "Add alarm at 12:00", "Set an alarm at ten forty five" },
  run = function(args)
    local h, m = parse_any(args.time)
    if not h then return "error: say the time, for example: add alarm at 7:30" end
    if h > 23 then return "error: use an hour from 0 to 23" end
    m = m or 0
    local ok, err = add(h, m)
    if ok then return "Alarm set for " .. label{ h = h, m = m } end
    return "error: " .. (err or "could not add")
  end,
}

voice.command{
  name = "sveglia_off",
  description = "Turn an alarm off in the Sveglia app (time: the alarm's time, e.g. 7:30)",
  params = { time = { type = "string", description = "the alarm's time, e.g. 7:30", required = true } },
  phrases = { "turn off [the] alarm [at|for] {time}", "cancel [the] alarm [at|for] {time}", "remove [the] alarm [at] {time}" },
  samples = { "Turn off the alarm at seven thirty", "Cancel the alarm at 06:15", "Remove the alarm at nine o'clock",
              "Turn off alarm 12:00" },
  run = function(args)
    local h, m = parse_any(args.time)
    if not h then return "error: say the time, for example: turn off the alarm at 7:30" end
    m = m or 0
    for _, a in ipairs(S.alarms) do
      if a.h == h and a.m == m then
        a.on = false; save(); arm(); render()
        return "Alarm at " .. label(a) .. " is off"
      end
    end
    return "error: no alarm at " .. pad2(h) .. ":" .. pad2(m)
  end,
}

voice.command{
  name = "sveglia_stop",
  description = "Stop the alarm that is ringing now in the Sveglia app",
  phrases = { "okay [stop the alarm]", "stop the alarm", "silence the alarm", "that's enough [alarm]" },
  samples = { "Okay stop the alarm", "Stop the alarm", "Silence the alarm", "Okay okay stop" },
  run = function()
    if not ringing then return "error: no alarm is ringing" end
    stop_ring(); ringing = false; render()
    return "Alarm stopped"
  end,
}
