#if UNIXD_PRIMETWEEN_SUPPORT
using System.Collections.Generic;
using PrimeTween;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BIEngine.UniXD {
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Button))]
    [AddComponentMenu("UI/Button Feedback")]
    public sealed class ButtonFeedback : UIBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler, ISubmitHandler {
        [SerializeField, Tooltip("留空时缩放按钮本身；有布局或其他缩放动画时可指定内部视觉节点。")]
        private RectTransform m_Target;
        [SerializeField, Range(0.5f, 1f), Tooltip("参考尺寸按钮的按下缩放；实际比例随目标尺寸调整。")]
        private float m_PressedScale = 0.94f;
        [SerializeField, Min(1f), Tooltip("参考按钮的最长边，使用根 Canvas 的 UI 单位，不受屏幕分辨率影响。")]
        private float m_ReferenceSize = 200f;
        [SerializeField, Range(0f, 0.5f), Tooltip("小按钮的最大收缩比例，默认最多缩小 12%。")]
        private float m_MaxScaleReduction = 0.12f;
        [SerializeField, Min(0)] private float m_PressDuration = 0.06f;
        [SerializeField, Min(0)] private float m_ReleaseDuration = 0.18f;
        private Button m_Button;
        private RectTransform m_AnimatedTarget;
        private Vector3 m_OriginalScale;
        private Vector3 m_PressedTargetScale;
        private Tween m_Tween;
        private List<CanvasGroup> m_CanvasGroupCache;
        private bool m_OwnsScale;
        private int m_PointerId = int.MinValue;

        public void OnPointerDown(PointerEventData eventData) {
            if (eventData.button != PointerEventData.InputButton.Left || m_PointerId != int.MinValue || !CanInteract()) return;
            CaptureScale();
            m_PointerId = eventData.pointerId;
            Press();
        }

        public void OnPointerUp(PointerEventData eventData) {
            if (eventData.pointerId != m_PointerId || eventData.button != PointerEventData.InputButton.Left) return;
            m_PointerId = int.MinValue;
            if (CanInteract()) Release();
            else ResetFeedback();
        }

        public void OnPointerExit(PointerEventData eventData) {
            if (m_OwnsScale && (m_PointerId == int.MinValue || m_PointerId == eventData.pointerId)) ResetFeedback();
        }

        public void OnSubmit(BaseEventData eventData) {
            if (!CanInteract()) return;
            CaptureScale();
            m_PointerId = int.MinValue;
            StopTween();
            if (m_PressDuration <= 0) {
                m_AnimatedTarget.localScale = m_PressedTargetScale;
                Release();
                return;
            }
            m_Tween = Tween.Scale(m_AnimatedTarget, m_PressedTargetScale, m_PressDuration,
                Ease.OutQuad, useUnscaledTime: true).OnComplete(this, feedback => feedback.Release());
        }

        private void Press() {
            StopTween();
            if (m_PressDuration <= 0) { m_AnimatedTarget.localScale = m_PressedTargetScale; return; }
            m_Tween = Tween.Scale(m_AnimatedTarget, m_PressedTargetScale, m_PressDuration,
                Ease.OutQuad, useUnscaledTime: true);
        }

        private void Release() {
            if (!m_OwnsScale) return;
            if (!CanInteract() || m_AnimatedTarget == null || GetTarget() != m_AnimatedTarget) { ResetFeedback(); return; }
            StopTween();
            if (m_ReleaseDuration <= 0) { ResetFeedback(); return; }
            m_Tween = Tween.Scale(m_AnimatedTarget, m_OriginalScale, m_ReleaseDuration,
                Ease.OutBack, useUnscaledTime: true).OnComplete(this, feedback => feedback.ResetFeedback());
        }

        private RectTransform GetTarget() => m_Target != null ? m_Target : (RectTransform)transform;

        private void CaptureScale() {
            var target = GetTarget();
            if (m_OwnsScale && m_AnimatedTarget != target) ResetFeedback();
            if (m_OwnsScale) return;
            m_AnimatedTarget = target;
            m_OriginalScale = target.localScale;
            m_PressedTargetScale = m_OriginalScale * GetPressedScale(target);
            m_OwnsScale = true;
        }

        private float GetPressedScale(RectTransform target) {
            // 在根 Canvas 空间测量未按下的视觉尺寸，包含目标与父级缩放，排除 CanvasScaler。
            var width = target.TransformVector(new Vector3(target.rect.width, 0f, 0f));
            var height = target.TransformVector(new Vector3(0f, target.rect.height, 0f));
            var canvas = target.GetComponentInParent<Canvas>();
            if (canvas != null) {
                var canvasTransform = canvas.rootCanvas.transform;
                width = canvasTransform.InverseTransformVector(width);
                height = canvasTransform.InverseTransformVector(height);
            }
            var size = Mathf.Max(width.magnitude, height.magnitude);
            var reduction = 1f - Mathf.Clamp01(m_PressedScale);
            if (size > 0.001f) reduction *= Mathf.Max(1f, m_ReferenceSize) / size;
            return 1f - Mathf.Clamp(reduction, 0f, Mathf.Clamp(m_MaxScaleReduction, 0f, 0.5f));
        }

        private bool CanInteract() {
            if (!isActiveAndEnabled) return false;
            if (m_Button == null) m_Button = GetComponent<Button>();
            return m_Button != null && m_Button.IsActive() && m_Button.IsInteractable();
        }

        protected override void OnCanvasGroupChanged() {
            base.OnCanvasGroupChanged();
            if (!m_OwnsScale) return;
            // 不依赖 Button 与本组件接收回调的顺序，直接使用 UGUI 的父级组交互规则。
            if (m_CanvasGroupCache == null) m_CanvasGroupCache = new List<CanvasGroup>();
            var current = transform;
            while (current != null) {
                current.GetComponents(m_CanvasGroupCache);
                foreach (var group in m_CanvasGroupCache) {
                    if (group.enabled && !group.interactable) { ResetFeedback(); return; }
                    if (group.ignoreParentGroups) return;
                }
                current = current.parent;
            }
        }

        private void StopTween() {
            if (m_Tween.isAlive) m_Tween.Stop();
            m_Tween = default;
        }

        /// <summary>恢复本次动效开始前的缩放，不假定美术原始缩放为 Vector3.one。</summary>
        public void ResetFeedback() {
            StopTween();
            if (m_OwnsScale && m_AnimatedTarget != null) m_AnimatedTarget.localScale = m_OriginalScale;
            m_OwnsScale = false;
            m_AnimatedTarget = null;
            m_PointerId = int.MinValue;
        }

        protected override void OnDisable() { ResetFeedback(); base.OnDisable(); }
        protected override void OnDestroy() { ResetFeedback(); base.OnDestroy(); }
        private void OnApplicationFocus(bool hasFocus) { if (!hasFocus) ResetFeedback(); }
        private void OnApplicationPause(bool paused) { if (paused) ResetFeedback(); }

        /// <summary>包含未激活节点，只补缺失组件，保留已有目标和动效配置。</summary>
        public static int AddToButtons(GameObject root, bool withUndo = false) {
            var added = 0;
            foreach (var button in root.GetComponentsInChildren<Button>(true)) {
                if (button.GetComponent<ButtonFeedback>() != null) continue;
#if UNITY_EDITOR
                if (withUndo) UnityEditor.Undo.AddComponent<ButtonFeedback>(button.gameObject);
                else
#endif
                    button.gameObject.AddComponent<ButtonFeedback>();
                added++;
            }
            return added;
        }
    }
}

#endif
