-- description: right-hand thumbs up opens the git panel; a fast open-palm push away closes it

-- OPEN: thumbs up with the RIGHT hand.
-- `thumbs-up` is the pose from the core-poses starter plugin (hand = any), so the hand is filtered here.
-- ui.open (not toggle) so a second thumbs up never closes the panel by accident.
gesture.on("thumbs-up", function(e)
  if e.phase ~= "start" or e.hand ~= "right" then return end
  ui.open("git")
end)

-- CLOSE: open palm facing away from you, pushed forward fast.
-- Detector runs ~36x/s: only field reads and number comparisons.
--   h.open                      all four fingers straight
--   h.palmFacing == "away"      palm points away from the eyes
--   h.vel.z > 1.0               forward speed in the head frame, m/s (1.0 = brisk)
--   |vel.x| < 0.6, |vel.y| < 0.7  reject diagonal arm swings (and swipes)
gesture.define("palm-push", {
    hand = "any",
    hold = 0,
    cooldown = 0.8,
    description = "open palm facing away, pushed forward fast",
  },
  function(h)
    return h.open
      and h.palmFacing == "away"
      and h.vel.z > 1.0
      and math.abs(h.vel.x) < 0.6
      and math.abs(h.vel.y) < 0.7
  end)

gesture.on("palm-push", function(e)
  if e.phase ~= "start" then return end
  ui.close("git")
end)
