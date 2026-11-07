using System;
using System.Collections.Generic;
using MetaCodex.Terminal;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace MetaCodex.App
{
    /// Index fingertips of the tracked hands (OVRSkeleton), for poking panels. Empty while hands are not
    /// tracked; the real hands stay visible through passthrough, so no hand mesh is drawn.
    public sealed class HandTips : MonoBehaviour
    {
        [SerializeField] private OVRSkeleton left;
        [SerializeField] private OVRSkeleton right;
        public readonly List<Vector3> Tips = new List<Vector3>(2);

        public void Set(OVRSkeleton l, OVRSkeleton r) { left = l; right = r; }

        private void Update()
        {
            Tips.Clear();
            Add(left);
            Add(right);
        }

        private void Add(OVRSkeleton s)
        {
            if (s == null || !s.IsInitialized || !s.IsDataValid || s.Bones == null) return;
            foreach (var b in s.Bones)
            {
                if ((b.Id == OVRSkeleton.BoneId.XRHand_IndexTip || b.Id == OVRSkeleton.BoneId.Hand_IndexTip) && b.Transform != null)
                {
                    Tips.Add(b.Transform.position);
                    return;
                }
            }
        }
    }
}
