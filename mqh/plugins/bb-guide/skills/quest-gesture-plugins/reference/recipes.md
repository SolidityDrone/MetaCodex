# Recipes (complete, tested patterns)

Each block is a whole file. Change names and reactions; keep the structure.

## 1. A pose only: "finger gun" opens the web panel (no Lua)

`/mnt/quest/plugins/finger-gun/plugin.json`
```json
{ "id": "finger-gun", "name": "Finger gun", "version": "1.0.0", "kind": "gesture",
  "description": "thumb up + index forward opens the web panel" }
```
`/mnt/quest/plugins/finger-gun/poses/finger-gun.json`
```json
{ "name": "finger-gun", "hand": "any",
  "fingers": { "thumb": 0, "index": 0, "middle": 1, "ring": 1, "little": 1 },
  "tolerance": 0.35, "thumbPointing": "up", "hold": 0.2, "cooldown": 1.0,
  "description": "thumb up, index straight, other fingers curled" }
```
Then bind it without code: `mcx-ui gesture bind finger-gun open:browser`
(or add `main.lua` with `gesture.on("finger-gun", function(e) if e.phase == "start" then ui.open("browser") end end)`).

## 2. Pose + reaction in Lua: thumbs up / down opens / closes git

```lua
-- description: thumbs up opens git, thumbs down closes it (poses from core-poses)
gesture.on("thumbs-up", function(e) if e.phase == "start" then ui.open("git") end end)
gesture.on("thumbs-down", function(e) if e.phase == "start" then ui.close("git") end end)
```
If `core-poses` might be disabled, define the poses in your plugin under new names
(`my-thumbs-up`) with the same fields as `core-poses/poses/thumbs-up.json`.

## 3. Motion: push away to close the focused panel (Lua detector)

```lua
-- description: push an open palm away from you to close the browser and git panels
gesture.define("push", { hand = "any", hold = 0, cooldown = 0.8,
                         description = "open palm facing away, pushed forward fast" },
  function(h)
    return h.open and h.palmFacing == "away" and h.vel.z > 1.0 and math.abs(h.vel.x) < 0.6
  end)

gesture.on("push", function(e)
  if e.phase ~= "start" then return end
  ui.close("browser")
  ui.close("git")
  ui.toast("Pushed away")
end)
```

## 4. Two hands: a "frame" with thumbs and index fingers starts the select tool

```lua
-- description: both hands in an L shape (thumb + index out), held still, turns on the select tool
local function L(h) return h.curl.thumb < 0.35 and h.curl.index < 0.3 and h.curl.middle > 0.6 and h.curl.ring > 0.6 end
gesture.define("frame", { hand = "both", hold = 0.3, cooldown = 1.5, description = "both hands in an L shape" },
  function(l, r)
    local dx = l.rel.x - r.rel.x
    return L(l) and L(r) and math.abs(dx) > 0.15 and l.speed < 0.3 and r.speed < 0.3
  end)
gesture.on("frame", function(e) if e.phase == "start" then ui.select(true) end end)
```

## 5. A wave (state machine inside the detector, no allocations)

```lua
-- description: wave an open hand (three direction changes within a second)
local last, flips, since = 0, 0, 0
gesture.define("wave", { hand = "right", hold = 0, cooldown = 2, description = "open hand waved side to side" },
  function(h)
    local t = app.time()
    if not h.open or h.palmFacing ~= "away" and h.palmFacing ~= "forward" then flips = 0; return false end
    local dir = (h.vel.x > 0.5 and 1) or (h.vel.x < -0.5 and -1) or 0
    if dir ~= 0 and dir ~= last then
      if t - since > 1.0 then flips = 0; since = t end
      flips = flips + 1; last = dir
    end
    if flips >= 3 then flips = 0; return true end
    return false
  end)
gesture.on("wave", function(e) if e.phase == "start" then ui.toast("Hello!") end end)
```

## 6. Sequence + confirmation: double thumbs-up runs tests, after a yes

```lua
-- description: thumbs up twice asks to run the test suite in the terminal
gesture.sequence({ "thumbs-up", "thumbs-up" }, 1.5, function(e)
  ui.modal("RUN TESTS", "Run npm test in the focused terminal?", { "RUN", "CANCEL" }, function(i)
    if i == 1 then terminal.run("npm test") end
  end)
end)
```

## 7. A toggle that survives restarts

```lua
-- description: horns toggles the float layout (remembered across restarts)
gesture.on("horns", function(e)
  if e.phase ~= "start" then return end
  local float = store.get("float") ~= "yes"
  store.set("float", float and "yes" or "no")
  app.action(float and "layout:float" or "layout:fixed")
end)
```

## 8. Emit your own event from a timer

```lua
-- description: a "hold still" gesture: an open right hand kept still for 1.5 s
local still = 0
app.every(0.1, function()
  local h = hands.get("right")
  if h.tracked and h.open and h.speed < 0.08 then still = still + 0.1 else still = 0 end
  if still >= 1.5 then still = -100; gesture.emit("hold-still", "right", "tap") end
  if not h.open then still = 0 end
end)
gesture.on("hold-still", function(e) ui.toast("Holding still") end)
```
