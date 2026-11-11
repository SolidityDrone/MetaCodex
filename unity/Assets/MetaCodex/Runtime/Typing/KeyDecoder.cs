using System;
using System.Collections.Generic;

namespace MetaCodex.Typing
{
    /// What is being typed into: it decides how much the letter statistics may weigh in.
    public enum KeyContext
    {
        /// Sentences (a message, a name, a search): English letter statistics help.
        Text,
        /// A terminal or a page: commands and code. The statistics weigh half; Tab, Ctrl, Esc and arrows are likely.
        Code,
        /// A password, a token, an API key: random characters. Where the finger came down, nothing else.
        Secret,
    }

    /// How likely each character is after the two before it: English as developers write it (sentences, commands,
    /// code words). 28 symbols: a-z, the space, everything else. A table of 28 x 28 x 28 bytes (-ln p x 32), made by
    /// tools/typing/build_letter_model.py from the project's own documentation.
    public sealed class LetterModel
    {
        public const int Symbols = 28, Space = 26, Other = 27;
        /// The keys that share the "everything else" symbol (digits and punctuation).
        public const float OtherKeys = 21f;
        private readonly byte[] _q;

        public LetterModel(byte[] table)
        {
            if (table == null || table.Length != Symbols * Symbols * Symbols) throw new ArgumentException("letter table: " + (table?.Length ?? 0) + " bytes");
            _q = table;
        }

        public static int Symbol(char c)
        {
            if (c >= 'a' && c <= 'z') return c - 'a';
            if (c >= 'A' && c <= 'Z') return c - 'A';
            if (c == ' ' || c == '\n' || c == '\r' || c == '\t' || c == '\0') return Space;
            return Other;
        }

        /// ln P(c | the two characters before it); '\0' stands for the start of the text.
        public float LogP(char prev2, char prev1, char c)
        {
            int s = Symbol(c);
            float lp = -_q[(Symbol(prev2) * Symbols + Symbol(prev1)) * Symbols + s] / 32f;
            return s == Other ? lp - (float)Math.Log(OtherKeys) : lp;
        }
    }

    /// Which key a press was meant for.
    ///
    /// The place a fingertip is tracked at is off by 5 to 11 mm on a headset (TouchInsight: 6.3 mm mean with Meta's
    /// own network, 10.6 mm for the plain tip; ring and little fingers worst), against keys 19 mm apart: the key
    /// under the tip is the wrong one about one time in four. So, as there (and as phone keyboards do), a press is
    /// not a point but a likelihood over the keys round it, and what was typed before says which of two close keys
    /// is the likely one:
    ///   score(key) = -distance² / 2σ² + weight x (ln P(key | the two characters before) - the best such)
    /// The second term is capped: it settles presses that fall between keys and never overrules one that lands within
    /// 5 mm of a key's centre, so what a careful finger aims at is what it gets.
    /// Each finger also has its own offset: the tracked tip of a finger lands off its key by about the same few
    /// millimetres every time (the tip joint is not the pad, a finger's own habit), and that part is learned from the
    /// presses themselves and taken out.
    public sealed class KeyDecoder
    {
        public readonly KeyboardLayout Layout;
        public LetterModel Letters;
        /// How far a tracked press scatters round its key (metres, one axis) for an index or middle finger.
        public float Sigma = 0.0058f;
        /// How much the letter statistics weigh. They never overrule a press that lands within Protect of a key's
        /// centre (metres): what they can take from a key is capped at what the place gives it there.
        public float PriorWeight = 0.6f, Protect = 0.005f;
        /// A press further than this from every key types nothing (metres from the key's body).
        public float MaxMiss = 0.016f;
        /// How fast a finger's offset is learned, and the largest it can be.
        public float LearnRate = 0.10f, MaxOffset = 0.007f;
        public readonly float[] OffX = new float[TapDetector.Count], OffY = new float[TapDetector.Count];

        public struct Choice
        {
            public KeyDef Key;
            /// ln of how well the place fits the key, the score with the statistics, and its share of all (0..1).
            public float LogLik, Score, P;
        }

        private readonly List<Choice> _choices = new List<Choice>();
        /// The keys the last press could have been, the chosen one first.
        public IReadOnlyList<Choice> Choices => _choices;

        public KeyDecoder(KeyboardLayout layout) { Layout = layout; }

        public void ForgetOffsets() { Array.Clear(OffX, 0, OffX.Length); Array.Clear(OffY, 0, OffY.Length); }

        private static readonly float[] FingerSigma = { 1.2f, 1f, 1f, 1.1f, 1.25f }; // thumb, index, middle, ring, little

        /// How far the point is from the key's body (a wide key is a bar, not a point), in metres on each axis.
        private static void Offset(KeyDef k, float x, float y, out float dx, out float dy)
        {
            float core = Math.Max(0f, (k.W - KeyboardLayout.Unit) * 0.5f);
            dx = Math.Max(0f, Math.Abs(x - k.Cx) - core);
            dy = y - k.Cy;
        }

        private float LogPrior(KeyDef k, char prev2, char prev1, bool shift, KeyContext ctx)
        {
            bool code = ctx == KeyContext.Code;
            switch (k.Kind)
            {
                case KbKind.Char:
                case KbKind.Space:
                {
                    if (ctx == KeyContext.Secret || Letters == null) return (float)Math.Log(1.0 / 48.0);
                    char c = k.Kind == KbKind.Space ? ' ' : (shift ? k.Shifted : k.Normal)[0];
                    return Letters.LogP(prev2, prev1, c);
                }
                case KbKind.Backspace: return (float)Math.Log(0.04);
                case KbKind.Enter: return (float)Math.Log(0.015);
                case KbKind.Shift: return (float)Math.Log(0.02);
                case KbKind.Tab: return (float)Math.Log(code ? 0.03 : 0.004);
                case KbKind.Ctrl: return (float)Math.Log(code ? 0.02 : 0.004);
                case KbKind.Alt: return (float)Math.Log(0.004);
                case KbKind.Escape: return (float)Math.Log(code ? 0.01 : 0.003);
                case KbKind.Left: case KbKind.Right: case KbKind.Up: case KbKind.Down: return (float)Math.Log(code ? 0.03 : 0.01);
                case KbKind.Caps: return (float)Math.Log(0.0008); // beside A: almost never meant
                case KbKind.Hide: return (float)Math.Log(0.0005);
                default: return (float)Math.Log(0.003);
            }
        }

        /// The key under a lifted finger (for the light on the key it would press): the place alone, with the
        /// finger's offset. Null off the keys.
        public KeyDef Under(int finger, float x, float y)
        {
            x -= OffX[finger]; y -= OffY[finger];
            var k = Layout.At(x, y);
            if (k != null) return k;
            KeyDef best = null;
            float bestD = MaxMiss * MaxMiss;
            foreach (var c in Layout.Keys)
            {
                float d = Outside(c, x, y);
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        /// The square of how far the point is outside the key's cell (0 inside).
        private static float Outside(KeyDef k, float x, float y)
        {
            float ex = Math.Max(0f, Math.Abs(x - k.Cx) - k.W * 0.5f), ey = Math.Max(0f, Math.Abs(y - k.Cy) - k.H * 0.5f);
            return ex * ex + ey * ey;
        }

        /// The key a press at (x, y) by `finger` was meant for; null when it came down off the keyboard. `prev2` and
        /// `prev1` are the two characters typed before ('\0' at the start), `shift` whether Shift or Caps Lock is on.
        public KeyDef Decode(int finger, float x, float y, char prev2, char prev1, bool shift, KeyContext ctx)
        {
            _choices.Clear();
            float px = x - OffX[finger], py = y - OffY[finger];
            bool thumb = finger % 5 == 0;
            float sigma = Sigma * FingerSigma[finger % 5];
            float inv = 1f / (2f * sigma * sigma);
            float reach = KeyboardLayout.Unit * 1.7f;
            float nearest = float.MaxValue;
            foreach (var k in Layout.Keys)
            {
                // a thumb presses the space bar and the keys beside it, nothing else
                if (thumb && k.Kind != KbKind.Space && k.Kind != KbKind.Alt) continue;
                Offset(k, px, py, out float dx, out float dy);
                if (dx > reach || Math.Abs(dy) > reach) continue;
                nearest = Math.Min(nearest, (float)Math.Sqrt(Outside(k, px, py)));
                _choices.Add(new Choice { Key = k, LogLik = -(dx * dx + dy * dy) * inv });
            }
            if (_choices.Count == 0 || nearest > MaxMiss) { _choices.Clear(); return null; }

            float weight = ctx == KeyContext.Code ? PriorWeight * 0.5f : PriorWeight;
            float cap = KeyboardLayout.Unit * (KeyboardLayout.Unit - 2f * Protect) * inv;
            float bestPrior = float.MinValue;
            var priors = new float[_choices.Count];
            for (int i = 0; i < _choices.Count; i++) { priors[i] = LogPrior(_choices[i].Key, prev2, prev1, shift, ctx); if (priors[i] > bestPrior) bestPrior = priors[i]; }
            float top = float.MinValue, sum = 0f;
            for (int i = 0; i < _choices.Count; i++)
            {
                var c = _choices[i];
                c.Score = c.LogLik + Math.Max(-cap, weight * (priors[i] - bestPrior));
                _choices[i] = c;
                if (c.Score > top) top = c.Score;
            }
            for (int i = 0; i < _choices.Count; i++) sum += (float)Math.Exp(_choices[i].Score - top);
            for (int i = 0; i < _choices.Count; i++) { var c = _choices[i]; c.P = (float)Math.Exp(c.Score - top) / sum; _choices[i] = c; }
            _choices.Sort((a, b) => b.Score.CompareTo(a.Score));
            return _choices[0].Key;
        }

        /// A press at (x, y) by `finger` was the key `key` (it was typed and not taken back): its finger's offset moves
        /// a little towards where that finger really lands.
        public void Learn(int finger, float x, float y, KeyDef key)
        {
            if (key == null) return;
            Offset(key, x - OffX[finger], y - OffY[finger], out _, out float dy);
            float rx = x - OffX[finger] - key.Cx;
            bool narrow = key.W <= KeyboardLayout.Unit * 1.3f;
            if (Math.Abs(dy) > KeyboardLayout.Unit * 0.75f || (narrow && Math.Abs(rx) > KeyboardLayout.Unit * 0.75f)) return; // not this key's press
            if (narrow) OffX[finger] = Clamp(OffX[finger] + LearnRate * rx);
            OffY[finger] = Clamp(OffY[finger] + LearnRate * dy);
        }

        private float Clamp(float v) => v < -MaxOffset ? -MaxOffset : v > MaxOffset ? MaxOffset : v;
    }
}
