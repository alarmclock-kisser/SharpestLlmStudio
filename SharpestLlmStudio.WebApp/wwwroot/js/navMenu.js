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
            if (element.tagName === 'INPUT' && element.type === 'file') {
                element.value = '';
            }
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

    // Setup a draggable vertical resize handle to control the top-panels content height.
    // handleId: id of the handle element; contentId: id of the top-panels content container;
    // minHeightPx: minimum height for the content; defaultHeightPx: optional initial height fallback.
    setupVerticalResizeHandle: function (handleId, contentId, minHeightPx, defaultHeightPx) {
        try {
            var handle = document.getElementById(handleId);
            var content = document.getElementById(contentId);
            if (!handle || !content) return;

            // Ensure the handle is visible and styled
            handle.style.cursor = 'row-resize';
            handle.style.userSelect = 'none';

            var dragging = false;
            var startY = 0;
            var startHeight = 0;

            var getFooterTop = function () {
                var footer = document.getElementById('chat-footer');
                return footer ? footer.getBoundingClientRect().top : window.innerHeight;
            };

            var clamp = function (v, a, b) { return Math.max(a, Math.min(b, v)); };

            var updateMaxHeightToFitFooter = function () {
                try {
                    var rect = content.getBoundingClientRect();
                    var top = rect.top;
                    var footerTop = getFooterTop();
                    var available = Math.max(120, Math.floor(footerTop - top - 12));
                    // if content currently has no explicit maxHeight, set default
                    if (!content.style.maxHeight || content.style.maxHeight === '0px') {
                        var initial = Number.isFinite(defaultHeightPx) ? defaultHeightPx : available;
                        content.style.maxHeight = clamp(initial, minHeightPx || 120, available) + 'px';
                    }
                    return available;
                } catch (e) { return window.innerHeight; }
            };

            var onPointerMove = function (ev) {
                if (!dragging) return;
                ev.preventDefault();
                var curY = ev.clientY || (ev.touches && ev.touches[0] && ev.touches[0].clientY) || 0;
                var delta = curY - startY;
                var newHeight = startHeight + delta;
                // cap to footer
                var rect = content.getBoundingClientRect();
                var top = rect.top;
                var footerTop = getFooterTop();
                var maxAllowed = Math.max(minHeightPx || 120, Math.floor(footerTop - top - 12));
                newHeight = clamp(newHeight, minHeightPx || 120, maxAllowed);
                content.style.maxHeight = newHeight + 'px';
                // ensure inner card body stays scrollable when needed
                var card = content.querySelector('.management-tabs-card');
                if (card) {
                    card.style.maxHeight = newHeight + 'px';
                }
            };

            var onPointerUp = function (ev) {
                if (!dragging) return;
                dragging = false;
                document.removeEventListener('pointermove', onPointerMove);
                document.removeEventListener('pointerup', onPointerUp);
                // final adjustment
                try { window.sharpestNavMenu.ensureTopPanelsExpandedToFooter(contentId, 'chat-footer'); } catch { }
            };

            handle.addEventListener('pointerdown', function (ev) {
                try {
                    ev.preventDefault();
                    dragging = true;
                    startY = ev.clientY || (ev.touches && ev.touches[0] && ev.touches[0].clientY) || 0;
                    // parse current height or fallback to computed
                    startHeight = parseInt((content.style.maxHeight || '').replace('px','')) || content.getBoundingClientRect().height || (defaultHeightPx || 300);
                    document.addEventListener('pointermove', onPointerMove, { passive: false });
                    document.addEventListener('pointerup', onPointerUp, { passive: false });
                } catch (e) { dragging = false; }
            }, { passive: false });

            // Recompute sizing on window resize
            var resizeHandler = function () {
                try {
                    window.sharpestNavMenu.ensureTopPanelsExpandedToFooter(contentId, 'chat-footer');
                } catch { }
            };

            window.addEventListener('resize', resizeHandler, { passive: true });

            // Initial sizing
            setTimeout(function () { updateMaxHeightToFitFooter(); try { window.sharpestNavMenu.ensureTopPanelsExpandedToFooter(contentId, 'chat-footer'); } catch{} }, 50);
        } catch (e) {
            // ignore
        }
    },

    // Ensure top panels expand to available space until footer; if content exceeds available space, make inner area scrollable.
    ensureTopPanelsExpandedToFooter: function (topPanelsContentId, footerId) {
        try {
            var content = document.getElementById(topPanelsContentId);
            var footer = document.getElementById(footerId);
            if (!content) return;

            // Compute available height from top of content to top of footer (or viewport bottom)
            var rect = content.getBoundingClientRect();
            var top = rect.top;
            var footerTop = footer ? footer.getBoundingClientRect().top : window.innerHeight;
            var available = Math.max(120, Math.floor(footerTop - top - 12)); // leave small gap

            // Apply max-height to content so it can expand to that size (up to footer)
            content.style.maxHeight = available + 'px';
            content.style.overflow = 'hidden';

            // Find inner card element (management-tabs-card) and size it to fit under the tab header
            var card = content.querySelector('.management-tabs-card');
            if (!card) return;

            // Determine header height inside the card to reserve space
            var header = card.querySelector('.management-tab-header');
            var headerHeight = header ? Math.ceil(header.getBoundingClientRect().height) : 48;

            // Calculate available height for the card body (leave small gap)
            var availableForCard = Math.max(120, available - headerHeight - 16);

            // Apply sizing: make card a column flex container and limit its max-height to the available space
            card.style.display = 'flex';
            card.style.flexDirection = 'column';
            card.style.maxHeight = availableForCard + headerHeight + 'px';

            // Make the body area scrollable if its content exceeds available space
            var body = card.querySelector('.management-tab-body');
            if (body) {
                body.style.overflowY = 'auto';
                body.style.flex = '1 1 auto';
            }
        } catch (e) {
            // ignore
        }
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
    },

    hideMicButtonMenu: function () {
        if (window._sharpestMicMenuOutsideHandler) {
            document.removeEventListener('mousedown', window._sharpestMicMenuOutsideHandler, true);
            document.removeEventListener('contextmenu', window._sharpestMicMenuOutsideHandler, true);
            window._sharpestMicMenuOutsideHandler = null;
        }

        const existing = document.getElementById('sharpest-mic-context-menu');
        if (existing) {
            existing.remove();
        }
    },

    showMicButtonMenu: function (clientX, clientY, uploadInputId) {
        window.sharpestNavMenu.hideMicButtonMenu();

        const menu = document.createElement('div');
        menu.id = 'sharpest-mic-context-menu';
        menu.style.position = 'fixed';
        menu.style.left = `${clientX}px`;
        menu.style.top = `${clientY}px`;
        menu.style.zIndex = '10000';
        menu.style.minWidth = '180px';
        menu.style.padding = '6px';
        menu.style.border = '1px solid rgba(0,0,0,0.14)';
        menu.style.borderRadius = '8px';
        menu.style.background = 'var(--rz-base-background-color, #fff)';
        menu.style.boxShadow = '0 10px 24px rgba(0,0,0,0.18)';

        const item = document.createElement('button');
        item.type = 'button';
        item.textContent = 'Upload Audio File';
        item.style.display = 'block';
        item.style.width = '100%';
        item.style.border = 'none';
        item.style.background = 'transparent';
        item.style.textAlign = 'left';
        item.style.padding = '8px 10px';
        item.style.borderRadius = '6px';
        item.style.cursor = 'pointer';
        item.style.color = 'inherit';
        item.onmouseenter = () => item.style.background = 'rgba(25, 118, 210, 0.08)';
        item.onmouseleave = () => item.style.background = 'transparent';
        item.onclick = (e) => {
            e.preventDefault();
            e.stopPropagation();
            window.sharpestNavMenu.hideMicButtonMenu();
            window.sharpestNavMenu.triggerClick(uploadInputId);
        };

        menu.appendChild(item);
        document.body.appendChild(menu);

        const rect = menu.getBoundingClientRect();
        const maxLeft = Math.max(8, window.innerWidth - rect.width - 8);
        const maxTop = Math.max(8, window.innerHeight - rect.height - 8);
        menu.style.left = `${Math.min(clientX, maxLeft)}px`;
        menu.style.top = `${Math.min(clientY, maxTop)}px`;

        window._sharpestMicMenuOutsideHandler = (e) => {
            if (!menu.contains(e.target)) {
                window.sharpestNavMenu.hideMicButtonMenu();
            }
        };

        setTimeout(() => {
            document.addEventListener('mousedown', window._sharpestMicMenuOutsideHandler, true);
            document.addEventListener('contextmenu', window._sharpestMicMenuOutsideHandler, true);
        }, 0);
    },

    setupMicButton: function (elementId, dotNetRef, uploadInputId) {
        const btn = document.getElementById(elementId);
        if (!btn || btn._micBound) return;
        btn._micBound = true;

        let holdTimer = null;
        let isHolding = false;

        const onDown = (e) => {
            if (e && typeof e.button === 'number' && e.button !== 0) return;
            window.sharpestNavMenu.hideMicButtonMenu();
            if (btn.disabled) return;
            isHolding = false;
            holdTimer = setTimeout(() => {
                isHolding = true;
                dotNetRef.invokeMethodAsync('OnMicHoldStart');
            }, 350);
        };

        const onUp = (e) => {
            if (e && typeof e.button === 'number' && e.button !== 0) return;
            if (holdTimer) {
                clearTimeout(holdTimer);
                holdTimer = null;
            }
            if (btn.disabled) return;
            if (isHolding) {
                isHolding = false;
                dotNetRef.invokeMethodAsync('OnMicHoldEnd');
            } else {
                dotNetRef.invokeMethodAsync('OnMicClick');
            }
        };

        const onContextMenu = (e) => {
            e.preventDefault();

            if (holdTimer) {
                clearTimeout(holdTimer);
                holdTimer = null;
            }

            if (isHolding) {
                isHolding = false;
                dotNetRef.invokeMethodAsync('OnMicHoldEnd');
            }

            if (btn.classList.contains('recording')) return;
            if (btn.disabled || !uploadInputId) return;
            window.sharpestNavMenu.showMicButtonMenu(e.clientX, e.clientY, uploadInputId);
        };

        const onLeave = (e) => {
            if (holdTimer) {
                clearTimeout(holdTimer);
                holdTimer = null;
            }
            if (isHolding) {
                isHolding = false;
                dotNetRef.invokeMethodAsync('OnMicHoldEnd');
            }
        };

        btn.addEventListener('mousedown', onDown);
        btn.addEventListener('mouseup', onUp);
        btn.addEventListener('mouseleave', onLeave);
        btn.addEventListener('contextmenu', onContextMenu);

        // Touch support
        btn.addEventListener('touchstart', (e) => { e.preventDefault(); onDown(e); }, { passive: false });
        btn.addEventListener('touchend', (e) => { e.preventDefault(); onUp(e); });
        btn.addEventListener('touchcancel', onLeave);
    }
};