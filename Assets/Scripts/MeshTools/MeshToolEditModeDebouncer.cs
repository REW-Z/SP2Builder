using System;
using UnityEngine;
using UnityEditor;

namespace MeshTools
{
    /// <summary>
    /// 给 ExecuteAlways / OnValidate 场景使用的编辑器防抖器。
    /// 它不会让布尔运算变成多线程，只负责把同一轮编辑器刷新中的多次请求合并成一次。
    /// 编辑模式默认只标记待执行，避免自动回调里运行重布尔导致 Unity Editor 假死。
    /// </summary>
    public sealed class MeshToolEditModeDebouncer
    {
        private UnityEngine.Object owner;
        private Action pendingAction;
        private bool queued;
        private bool running;
        private double requestedAt;
        private double editModeDelaySeconds = 0.15d;
        private bool autoRunInEditMode = true;

        /// <summary>
        /// 请求执行一次重建；编辑模式下会等请求安静一小段时间，运行模式下立即执行。
        /// </summary>
        public void Request(
            UnityEngine.Object owner,
            Action action,
            double editModeDelaySeconds = 0.15d,
            bool autoRunInEditMode = false)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            // 重建过程中给 sharedMesh 赋值可能同步触发 OnValidate。
            // 这类重入请求通常来自本次重建自身，直接忽略，避免形成编辑器刷新循环。
            if (running)
            {
                return;
            }

            this.owner = owner;
            pendingAction = action;

            requestedAt = EditorApplication.timeSinceStartup;
            this.editModeDelaySeconds = Math.Max(0d, editModeDelaySeconds);
            this.autoRunInEditMode = autoRunInEditMode;
            queued = true;

            if (!autoRunInEditMode)
            {
                Craft.UnregisterEditorUpdate(RunPendingWhenQuiet);
                EditorApplication.delayCall -= RunPendingNow;
                return;
            }

            Craft.RegisterEditorUpdate(RunPendingWhenQuiet);
            return;
        }

        /// <summary>
        /// 清理待执行请求，通常在 OnDisable 或 OnDestroy 中调用。
        /// </summary>
        public void Cancel()
        {
            Craft.UnregisterEditorUpdate(RunPendingWhenQuiet);
            EditorApplication.delayCall -= RunPendingNow;
            queued = false;
            pendingAction = null;
            owner = null;
        }

        /// <summary>
        /// 立即执行当前待处理请求；适合用在编辑器按钮或 ContextMenu 中。
        /// </summary>
        public void Flush()
        {
            Craft.UnregisterEditorUpdate(RunPendingWhenQuiet);
            EditorApplication.delayCall -= RunPendingNow;
            if (!queued)
            {
                return;
            }

            queued = false;
            Action action = pendingAction;
            pendingAction = null;

            if (owner == null || action == null)
            {
                return;
            }

            RunNow(action);
        }

        /// <summary>
        /// 是否存在等待执行的请求。
        /// </summary>
        public bool HasPending
        {
            get { return queued; }
        }

        /// <summary>
        /// 等到编辑器请求安静后，执行最后一次重建请求。
        /// </summary>
        private void RunPendingWhenQuiet()
        {
            if (!queued)
            {
                Craft.UnregisterEditorUpdate(RunPendingWhenQuiet);
                return;
            }

            if (EditorApplication.timeSinceStartup - requestedAt < editModeDelaySeconds)
            {
                return;
            }

            if (!autoRunInEditMode)
            {
                Craft.UnregisterEditorUpdate(RunPendingWhenQuiet);
                return;
            }

            Craft.UnregisterEditorUpdate(RunPendingWhenQuiet);
            EditorApplication.delayCall -= RunPendingNow;
            EditorApplication.delayCall += RunPendingNow;
        }

        /// <summary>
        /// 在 delayCall 中执行重建，避免直接卡在 EditorApplication.update 回调里。
        /// </summary>
        private void RunPendingNow()
        {
            EditorApplication.delayCall -= RunPendingNow;

            if (!queued)
            {
                return;
            }

            queued = false;
            Action action = pendingAction;
            pendingAction = null;

            if (owner == null || action == null)
            {
                return;
            }

            RunNow(action);
        }

        /// <summary>
        /// 带重入保护地执行实际重建逻辑。
        /// </summary>
        private void RunNow(Action action)
        {
            if (running)
            {
                return;
            }

            running = true;
            try
            {
                action();
            }
            finally
            {
                running = false;
            }
        }
    }
}
