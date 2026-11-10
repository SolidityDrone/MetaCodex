// MetaCodex: a panel being cast, or taken back (UiPanel / UiPanelFx). Included by every shader that draws inside a
// panel (URP HLSL and the CG TextMeshPro copy, so plain HLSL only). Per panel slot (the renderer's _PanelIndex), all
// zero at rest:
//   _PanelBend[slot].x  the bend radius drawn now (0 = flat)
//   _PanelFx[slot]      x = 1 while the panel is cast or taken back, y = a flash of light 0..1, z = everything hidden
//                       0..1 (the last moment of a closing), w = its own corner brackets hidden 0..1 (the transition
//                       draws them while they fly in or let go)
//   _PanelScan[slot]    the scan plane in world space: xyz = its normal (the way the line moves while the panel
//                       opens), w = its offset. A point p is built when dot(p, xyz) < w.
// A panel is built by a line of light that crosses it once (cast by the orb): what the line has passed is there, hot
// for a few centimetres behind it; what it has not reached does not exist yet. Closing runs the line back.
#ifndef MCX_PANEL_FX_INCLUDED
#define MCX_PANEL_FX_INCLUDED

float4 _PanelBend[128];
float4 _PanelFx[128];
float4 _PanelScan[128];

float4 McxPanelFx(float index) { return index >= 0.0 ? _PanelFx[(int)index] : float4(0, 0, 0, 0); }

// metres behind the scan line (> 0: built); 10 when the panel is at rest or the renderer is in no panel
float McxBehindScan(float index, float3 positionWS)
{
    if (index < 0.0) return 10.0;
    float4 fx = _PanelFx[(int)index];
    if (fx.x < 0.5) return 10.0;
    float4 s = _PanelScan[(int)index];
    return s.w - dot(positionWS, s.xyz);
}

// a control, a text or a picture `behind` metres behind the line: how much of it is there (0..1) ...
float McxBuilt(float behind) { return saturate((behind - 0.012) / 0.012); }
// ... and how hot it still runs (1 at the line, gone 10 cm behind it)
float McxHeat(float behind) { return behind > 0.012 ? exp(-(behind - 0.012) / 0.03) : 0.0; }

#endif
