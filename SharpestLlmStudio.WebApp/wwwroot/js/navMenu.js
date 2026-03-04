window.sharpestNavMenu = {
    copyText: async function (text) {
        if (navigator.clipboard && window.isSecureContext) {
            await navigator.clipboard.writeText(text ?? "");
            return;
        }

        const textArea = document.createElement('textarea');
        textArea.value = text ?? "";
        textArea.style.position = 'fixed';
        textArea.style.left = '-9999px';
        document.body.appendChild(textArea);
        textArea.focus();
        textArea.select();
        document.execCommand('copy');
        textArea.remove();
    },

    downloadTextAsFile: function (fileName, text) {
        const blob = new Blob([text ?? ""], { type: "text/plain;charset=utf-8" });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = fileName || `logs_${new Date().toISOString().replace(/[:.]/g, '-')}.log`;
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(url);
    },

    scrollToBottom: function (elementId) {
        const element = document.getElementById(elementId);
        if (!element) {
            return;
        }

        element.scrollTop = element.scrollHeight;
    },

    triggerClick: function (elementId) {
        const element = document.getElementById(elementId);
        if (element) {
            element.click();
        }
    },

    setupPromptEnter: function (elementId, dotNetRef) {
        const el = document.getElementById(elementId);
        if (!el) return;
        if (el._promptEnterHandler) {
            el.removeEventListener('keydown', el._promptEnterHandler);
        }
        el._promptEnterHandler = function (e) {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                dotNetRef.invokeMethodAsync('OnEnterPressed');
            }
        };
        el.addEventListener('keydown', el._promptEnterHandler);
    },

    setupConditionalAutoScroll: function (elementId, thresholdPxOrRatio) {
        const el = document.getElementById(elementId);
        if (!el) return;

        const updateSticky = function () {
            const raw = Number.isFinite(thresholdPxOrRatio) ? thresholdPxOrRatio : 0.1;
            const threshold = raw > 0 && raw <= 1
                ? Math.max(24, el.clientHeight * raw)
                : Math.max(24, raw);
            const distance = el.scrollHeight - el.scrollTop - el.clientHeight;
            el._stickToBottom = distance <= threshold;
        };

        if (el._autoScrollHandler) {
            el.removeEventListener('scroll', el._autoScrollHandler);
        }

        el._autoScrollHandler = updateSticky;
        el.addEventListener('scroll', el._autoScrollHandler, { passive: true });

        updateSticky();
    },

    autoScrollIfSticky: function (elementId) {
        const el = document.getElementById(elementId);
        if (!el) return;

        const distance = el.scrollHeight - el.scrollTop - el.clientHeight;
        const ratioThreshold = Math.max(24, el.clientHeight * 0.1);
        if (distance <= ratioThreshold) {
            el._stickToBottom = true;
        }

        if (el._stickToBottom !== false) {
            el.scrollTop = el.scrollHeight;
        }
    },

    setupVerticalResizeHandle: function (handleId, targetId, minHeight, maxHeight) {
        const handle = document.getElementById(handleId);
        const target = document.getElementById(targetId);
        if (!handle || !target) return;

        if (handle._resizeAttached) return;
        handle._resizeAttached = true;

        let startY = 0;
        let startHeight = 0;

        const onMove = (ev) => {
            const delta = ev.clientY - startY;
            const next = Math.min(maxHeight ?? 900, Math.max(minHeight ?? 160, startHeight + delta));
            target.style.maxHeight = `${next}px`;
            target.style.height = `${next}px`;
        };

        const onUp = () => {
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            document.body.style.userSelect = '';
        };

        handle.addEventListener('mousedown', (ev) => {
            ev.preventDefault();
            startY = ev.clientY;
            startHeight = target.getBoundingClientRect().height;
            document.body.style.userSelect = 'none';
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
        });
    },

    getImageDimensionsFromDataUrl: function (dataUrl) {
        return new Promise((resolve) => {
            try {
                if (!dataUrl || typeof dataUrl !== 'string' || !dataUrl.startsWith('data:image/')) {
                    resolve([0, 0]);
                    return;
                }

                const img = new Image();
                img.onload = function () {
                    resolve([img.naturalWidth || 0, img.naturalHeight || 0]);
                };
                img.onerror = function () {
                    resolve([0, 0]);
                };
                img.src = dataUrl;
            } catch {
                resolve([0, 0]);
            }
        });
    }
};