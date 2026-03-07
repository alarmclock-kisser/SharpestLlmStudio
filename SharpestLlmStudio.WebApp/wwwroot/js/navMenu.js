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

    bindFooterOffset: function (scrollerId, footerId, minOffsetPx) {
        const scroller = document.getElementById(scrollerId);
        const footer = document.getElementById(footerId);
        if (!scroller || !footer) return;

        const update = function () {
            const footerHeight = Math.ceil(footer.getBoundingClientRect().height || 0);
            const minHeight = Number.isFinite(minOffsetPx) ? minOffsetPx : 0;
            const offset = Math.max(minHeight, footerHeight);
            scroller.style.setProperty('--chat-footer-offset', `${offset}px`);
        };

        update();

        if (scroller._footerResizeObserver) {
            scroller._footerResizeObserver.disconnect();
        }

        if (typeof ResizeObserver !== 'undefined') {
            const resizeObserver = new ResizeObserver(() => update());
            resizeObserver.observe(footer);
            scroller._footerResizeObserver = resizeObserver;
        }

        if (scroller._footerWindowResizeHandler) {
            window.removeEventListener('resize', scroller._footerWindowResizeHandler);
        }

        scroller._footerWindowResizeHandler = update;
        window.addEventListener('resize', scroller._footerWindowResizeHandler, { passive: true });
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
    },

    setupClipboardImagePaste: function (elementId, dotNetRef) {
        const el = document.getElementById(elementId);
        if (!el) return;
        if (el._clipboardPasteHandler) {
            el.removeEventListener('paste', el._clipboardPasteHandler);
        }
        el._clipboardPasteHandler = function (e) {
            const items = (e.clipboardData || window.clipboardData)?.items;
            if (!items) return;
            for (let i = 0; i < items.length; i++) {
                const item = items[i];
                if (item.type.indexOf('image') !== -1) {
                    e.preventDefault();
                    const blob = item.getAsFile();
                    if (!blob) continue;
                    const reader = new FileReader();
                    reader.onloadend = function () {
                        const dataUrl = reader.result;
                        if (dataUrl && typeof dataUrl === 'string' && dataUrl.startsWith('data:image/')) {
                            dotNetRef.invokeMethodAsync('OnClipboardImagePasted', dataUrl, blob.type || 'image/png');
                        }
                    };
                    reader.readAsDataURL(blob);
                    break;
                }
            }
        };
        el.addEventListener('paste', el._clipboardPasteHandler);
    },

    getPrefersDarkMode: function () {
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
    },

    applyDarkMode: function (isDark) {
        document.documentElement.setAttribute('data-theme', isDark ? 'dark' : 'light');

        // Swap Radzen theme stylesheet
        const radzenLink = document.querySelector('link[href*="material-"]');
        if (radzenLink) {
            if (isDark) {
                radzenLink.href = radzenLink.href.replace('material-base.css', 'material-dark-base.css');
            } else {
                radzenLink.href = radzenLink.href.replace('material-dark-base.css', 'material-base.css');
            }
        }
    }
};