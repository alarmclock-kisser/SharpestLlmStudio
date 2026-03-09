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

        element._stickToBottom = true;
        element._programmaticScroll = true;

        const apply = () => {
            element.scrollTop = element.scrollHeight;
        };

        apply();
        if (typeof requestAnimationFrame === 'function') {
            requestAnimationFrame(apply);
        }
        setTimeout(() => {
            apply();
            element._programmaticScroll = false;
        }, 0);
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

        const getThreshold = function () {
            const raw = Number.isFinite(thresholdPxOrRatio) ? thresholdPxOrRatio : 0.1;
            return raw > 0 && raw <= 1
                ? Math.max(24, el.clientHeight * raw)
                : Math.max(24, raw);
        };

        const isNearBottom = function () {
            const distance = el.scrollHeight - el.scrollTop - el.clientHeight;
            return distance <= getThreshold();
        };

        const updateSticky = function () {
            if (el._programmaticScroll) return;
            el._stickToBottom = isNearBottom();
        };

        const maintainStickyBottom = function () {
            if (el._stickToBottom === false) {
                return;
            }

            window.sharpestNavMenu.scrollToBottom(elementId);

            if (typeof el._sharpestScrollBottomHandler === 'function') {
                el._sharpestScrollBottomHandler();
            }
        };

        if (el._autoScrollHandler) {
            el.removeEventListener('scroll', el._autoScrollHandler);
        }

        el._autoScrollHandler = updateSticky;
        el.addEventListener('scroll', el._autoScrollHandler, { passive: true });

        if (el._conditionalAutoScrollResizeObserver) {
            el._conditionalAutoScrollResizeObserver.disconnect();
        }

        if (typeof ResizeObserver !== 'undefined') {
            const resizeObserver = new ResizeObserver(() => {
                if (el._stickToBottom !== false) {
                    maintainStickyBottom();
                    return;
                }

                updateSticky();
            });

            resizeObserver.observe(el);
            if (el.firstElementChild) {
                resizeObserver.observe(el.firstElementChild);
            }

            el._conditionalAutoScrollResizeObserver = resizeObserver;
        }

        if (el._conditionalAutoScrollMutationObserver) {
            el._conditionalAutoScrollMutationObserver.disconnect();
        }

        if (typeof MutationObserver !== 'undefined') {
            const mutationObserver = new MutationObserver(() => {
                if (el._stickToBottom !== false) {
                    maintainStickyBottom();
                    return;
                }

                updateSticky();
            });

            mutationObserver.observe(el, { childList: true, subtree: true, characterData: true });
            el._conditionalAutoScrollMutationObserver = mutationObserver;
        }

        updateSticky();
    },

    autoScrollIfSticky: function (elementId) {
        const el = document.getElementById(elementId);
        if (!el) return;

        if (el._stickToBottom !== false) {
            window.sharpestNavMenu.scrollToBottom(elementId);
        }
    },

    setupScrollToBottomButton: function (scrollerId, buttonId) {
        const scroller = document.getElementById(scrollerId);
        if (!scroller) return;

        const update = function () {
            const button = document.getElementById(buttonId);
            if (!button) return;

            const scrollable = scroller.scrollHeight > scroller.clientHeight + 8;
            const atBottom = (scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight) <= 24;
            button.style.display = scrollable && !atBottom ? 'inline-flex' : 'none';
        };

        if (scroller._sharpestScrollBottomHandler) {
            scroller.removeEventListener('scroll', scroller._sharpestScrollBottomHandler);
        }

        scroller._sharpestScrollBottomHandler = update;
        scroller.addEventListener('scroll', scroller._sharpestScrollBottomHandler, { passive: true });

        if (scroller._sharpestScrollBottomResizeObserver) {
            scroller._sharpestScrollBottomResizeObserver.disconnect();
        }

        if (typeof ResizeObserver !== 'undefined') {
            const resizeObserver = new ResizeObserver(update);
            resizeObserver.observe(scroller);
            if (scroller.firstElementChild) {
                resizeObserver.observe(scroller.firstElementChild);
            }
            scroller._sharpestScrollBottomResizeObserver = resizeObserver;
        }

        if (scroller._sharpestScrollBottomMutationObserver) {
            scroller._sharpestScrollBottomMutationObserver.disconnect();
        }

        if (typeof MutationObserver !== 'undefined') {
            const mutationObserver = new MutationObserver(update);
            mutationObserver.observe(scroller, { childList: true, subtree: true, characterData: true });
            scroller._sharpestScrollBottomMutationObserver = mutationObserver;
        }

        update();
        if (typeof requestAnimationFrame === 'function') {
            requestAnimationFrame(update);
        }
        setTimeout(update, 0);
        setTimeout(update, 150);
    },

    setupThinkBlocks: function (scrollerId) {
        const scroller = document.getElementById(scrollerId);
        if (!scroller) return;

        const preferredOpen = window._sharpestThinkBlocksExpanded === true;
        const detailsList = scroller.querySelectorAll('details.think-block');

        detailsList.forEach((detail) => {
            if (!detail._sharpestThinkBound) {
                detail.addEventListener('toggle', () => {
                    window._sharpestThinkBlocksExpanded = detail.open;

                    if (detail.open && scroller._stickToBottom !== false) {
                        scroller._programmaticScroll = true;
                        requestAnimationFrame(() => {
                            scroller.scrollTop = scroller.scrollHeight;
                            scroller._programmaticScroll = false;
                        });
                    }
                });

                detail._sharpestThinkBound = true;
            }

            if (detail.dataset.thinkInit !== '1') {
                detail.open = preferredOpen;
                detail.dataset.thinkInit = '1';
            }
        });
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
        window._sharpestClipboardDotNetRef = dotNetRef;

        if (window._sharpestClipboardPasteHandler) {
            return;
        }

        const getImageBlobFromClipboard = (clipboardData) => {
            if (!clipboardData) return null;

            const items = clipboardData.items;
            if (items) {
                for (let i = 0; i < items.length; i++) {
                    const item = items[i];
                    if (item && typeof item.type === 'string' && item.type.startsWith('image/')) {
                        const blob = item.getAsFile();
                        if (blob) {
                            return blob;
                        }
                    }
                }
            }

            const files = clipboardData.files;
            if (files) {
                for (let i = 0; i < files.length; i++) {
                    const file = files[i];
                    if (file && typeof file.type === 'string' && file.type.startsWith('image/')) {
                        return file;
                    }
                }
            }

            return null;
        };

        window._sharpestClipboardPasteHandler = function (e) {
            const blob = getImageBlobFromClipboard(e.clipboardData || window.clipboardData);
            if (!blob) {
                return;
            }

            e.preventDefault();
            e.stopImmediatePropagation();
            window._sharpestLastPasteHandled = Date.now();

            const reader = new FileReader();
            reader.onloadend = function () {
                const dataUrl = reader.result;
                if (!dataUrl || typeof dataUrl !== 'string' || !dataUrl.startsWith('data:image/')) {
                    return;
                }

                const ref = window._sharpestClipboardDotNetRef;
                if (ref) {
                    ref.invokeMethodAsync('OnClipboardImagePasted', dataUrl, blob.type || 'image/png');
                }
            };

            reader.readAsDataURL(blob);
        };

        document.addEventListener('paste', window._sharpestClipboardPasteHandler, true);

        // Also intercept via Clipboard API for Win+V / clipboard history scenarios
        if (navigator.clipboard && navigator.clipboard.read) {
            document.addEventListener('keydown', function (e) {
                // Win+V triggers OS clipboard history — after selection, a paste event fires.
                // Some browsers suppress the paste event for clipboard history items.
                // This handler catches that scenario by reading the Clipboard API directly.
                if ((e.ctrlKey || e.metaKey) && e.key === 'v') {
                    // Let the paste event handler try first; use a short delay as fallback
                    setTimeout(async () => {
                        // Only act if the paste handler didn't already fire (guard via a flag)
                        if (window._sharpestLastPasteHandled &&
                            (Date.now() - window._sharpestLastPasteHandled) < 500) {
                            return;
                        }
                        try {
                            const items = await navigator.clipboard.read();
                            for (const item of items) {
                                const imageType = item.types.find(t => t.startsWith('image/'));
                                if (imageType) {
                                    const blob = await item.getType(imageType);
                                    const reader = new FileReader();
                                    reader.onloadend = function () {
                                        const dataUrl = reader.result;
                                        if (dataUrl && typeof dataUrl === 'string' && dataUrl.startsWith('data:image/')) {
                                            const ref = window._sharpestClipboardDotNetRef;
                                            if (ref) {
                                                ref.invokeMethodAsync('OnClipboardImagePasted', dataUrl, imageType);
                                            }
                                        }
                                    };
                                    reader.readAsDataURL(blob);
                                    break;
                                }
                            }
                        } catch { /* clipboard read not permitted or empty */ }
                    }, 150);
                }
            }, true);
        }
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