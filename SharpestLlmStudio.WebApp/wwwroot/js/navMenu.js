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

    hasFunction: function (path) {
        if (!path || typeof path !== 'string') {
            return false;
        }

        const segments = path.split('.');
        let current = window;
        for (const segment of segments) {
            if (!current || !(segment in current)) {
                return false;
            }

            current = current[segment];
        }

        return typeof current === 'function';
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

    isElementFocused: function (elementId) {
        const element = document.getElementById(elementId);
        return !!element && document.activeElement === element;
    },

    focusElementIfExists: function (elementId) {
        const element = document.getElementById(elementId);
        if (!element || typeof element.focus !== 'function') {
            return false;
        }

        try {
            element.focus({ preventScroll: true });
        } catch {
            element.focus();
        }

        try {
            if (typeof element.value === 'string' && typeof element.setSelectionRange === 'function') {
                const len = element.value.length;
                element.setSelectionRange(len, len);
            }
        } catch {
        }

        return document.activeElement === element;
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
    },

    setupClickerLoopEscape: function (dotNetRef, isActive) {
        window._sharpestClickerLoopDotNetRef = dotNetRef;
        window._sharpestClickerLoopEscapeActive = !!isActive;

        if (window._sharpestClickerLoopEscapeBound) {
            return;
        }

        window._sharpestClickerLoopEscapeBound = true;
        document.addEventListener('keydown', function (e) {
            if (!window._sharpestClickerLoopEscapeActive || e.key !== 'Escape') {
                return;
            }

            if (document.getElementById('sharpest-clicker-confirm-popup')) {
                return;
            }

            e.preventDefault();
            e.stopPropagation();

            const ref = window._sharpestClickerLoopDotNetRef;
            if (ref) {
                ref.invokeMethodAsync('OnClickerEscapePressed');
            }
        }, true);
    },

    cancelClickerProtectedZoneSelection: function () {
        if (window._sharpestClickerZoneSelectionCleanup) {
            try { window._sharpestClickerZoneSelectionCleanup(); } catch { }
            window._sharpestClickerZoneSelectionCleanup = null;
        }
    },

    armClickerProtectedZoneSelection: function (stageId, dotNetRef) {
        const stage = document.getElementById(stageId);
        if (!stage) {
            return false;
        }

        window.sharpestNavMenu.cancelClickerProtectedZoneSelection();

        const clamp = function (value, min, max) {
            return Math.min(max, Math.max(min, value));
        };

        const getRelativePoint = function (clientX, clientY) {
            const rect = stage.getBoundingClientRect();
            return {
                rect,
                x: clamp(clientX - rect.left, 0, rect.width),
                y: clamp(clientY - rect.top, 0, rect.height)
            };
        };

        const draft = document.createElement('div');
        draft.className = 'clicker-protected-zone-draft';
        draft.style.display = 'none';
        stage.appendChild(draft);
        stage.classList.add('clicker-zone-selection-active');

        let pointerId = null;
        let startX = 0;
        let startY = 0;
        let dragging = false;

        const updateDraft = function (x1, y1, x2, y2) {
            const left = Math.min(x1, x2);
            const top = Math.min(y1, y2);
            const width = Math.max(1, Math.abs(x2 - x1));
            const height = Math.max(1, Math.abs(y2 - y1));

            draft.style.display = 'block';
            draft.style.left = `${left}px`;
            draft.style.top = `${top}px`;
            draft.style.width = `${width}px`;
            draft.style.height = `${height}px`;
        };

        const cleanup = function () {
            document.removeEventListener('pointermove', onPointerMove, true);
            document.removeEventListener('pointerup', onPointerUp, true);
            document.removeEventListener('keydown', onKeyDown, true);
            stage.removeEventListener('pointerdown', onPointerDown, true);

            if (pointerId !== null && stage.releasePointerCapture) {
                try { stage.releasePointerCapture(pointerId); } catch { }
            }

            stage.classList.remove('clicker-zone-selection-active');
            if (draft.parentElement) {
                draft.remove();
            }

            if (window._sharpestClickerZoneSelectionCleanup === cleanup) {
                window._sharpestClickerZoneSelectionCleanup = null;
            }
        };

        const finalize = function (x1, y1, x2, y2, treatAsPoint) {
            const rect = stage.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) {
                cleanup();
                return;
            }

            let left = Math.min(x1, x2);
            let top = Math.min(y1, y2);
            let right = Math.max(x1, x2);
            let bottom = Math.max(y1, y2);

            if (treatAsPoint || (Math.abs(right - left) < 6 && Math.abs(bottom - top) < 6)) {
                const halfWidth = Math.max(12, rect.width * 0.03);
                const halfHeight = Math.max(12, rect.height * 0.03);
                const centerX = left;
                const centerY = top;
                left = clamp(centerX - halfWidth, 0, rect.width);
                right = clamp(centerX + halfWidth, 0, rect.width);
                top = clamp(centerY - halfHeight, 0, rect.height);
                bottom = clamp(centerY + halfHeight, 0, rect.height);
            }

            const normalizedLeft = clamp(Math.round((left / rect.width) * 1000), 0, 999);
            const normalizedTop = clamp(Math.round((top / rect.height) * 1000), 0, 999);
            const normalizedRight = clamp(Math.round((right / rect.width) * 1000), normalizedLeft + 1, 1000);
            const normalizedBottom = clamp(Math.round((bottom / rect.height) * 1000), normalizedTop + 1, 1000);

            cleanup();

            if (dotNetRef) {
                dotNetRef.invokeMethodAsync(
                    'OnClickerProtectedZoneSelected',
                    normalizedLeft,
                    normalizedTop,
                    Math.max(1, normalizedRight - normalizedLeft),
                    Math.max(1, normalizedBottom - normalizedTop));
            }
        };

        const onPointerDown = function (e) {
            if (e.button !== 0) {
                return;
            }

            e.preventDefault();
            e.stopPropagation();

            const point = getRelativePoint(e.clientX, e.clientY);
            pointerId = e.pointerId;
            startX = point.x;
            startY = point.y;
            dragging = true;
            updateDraft(startX, startY, startX, startY);

            if (stage.setPointerCapture) {
                try { stage.setPointerCapture(pointerId); } catch { }
            }
        };

        const onPointerMove = function (e) {
            if (!dragging) {
                return;
            }

            e.preventDefault();

            const point = getRelativePoint(e.clientX, e.clientY);
            updateDraft(startX, startY, point.x, point.y);
        };

        const onPointerUp = function (e) {
            if (!dragging) {
                return;
            }

            e.preventDefault();
            e.stopPropagation();

            dragging = false;
            const point = getRelativePoint(e.clientX, e.clientY);
            const treatAsPoint = Math.abs(point.x - startX) < 6 && Math.abs(point.y - startY) < 6;
            finalize(startX, startY, point.x, point.y, treatAsPoint);
        };

        const onKeyDown = function (e) {
            if (e.key !== 'Escape') {
                return;
            }

            e.preventDefault();
            e.stopPropagation();
            cleanup();

            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('OnClickerProtectedZoneSelectionCanceled');
            }
        };

        window._sharpestClickerZoneSelectionCleanup = cleanup;
        stage.addEventListener('pointerdown', onPointerDown, true);
        document.addEventListener('pointermove', onPointerMove, true);
        document.addEventListener('pointerup', onPointerUp, true);
        document.addEventListener('keydown', onKeyDown, true);
        return true;
    },

    dismissClickerConfirmation: function () {
        if (window._sharpestClickerConfirmCleanup) {
            try { window._sharpestClickerConfirmCleanup(); } catch { }
            window._sharpestClickerConfirmCleanup = null;
        }
    },

    awaitClickerConfirmation: function (screenX, screenY, isLoopRunning) {
        window.sharpestNavMenu.dismissClickerConfirmation();

        if (!window._sharpestClickerMouseTrackerBound) {
            window._sharpestClickerMouseTrackerBound = true;
            window._sharpestClickerMousePos = { x: Math.max(16, Number(screenX) || 16), y: Math.max(16, Number(screenY) || 16) };
            window.addEventListener('mousemove', function (e) {
                window._sharpestClickerMousePos = { x: e.clientX, y: e.clientY };
            }, { passive: true });
        }

        return new Promise((resolve) => {
            const mouse = window._sharpestClickerMousePos || { x: Number(screenX) || 24, y: Number(screenY) || 24 };
            const popup = document.createElement('div');
            popup.id = 'sharpest-clicker-confirm-popup';
            popup.style.position = 'fixed';
            popup.style.zIndex = '100000';
            popup.style.left = '0px';
            popup.style.top = '0px';
            popup.style.maxWidth = '320px';
            popup.style.padding = '10px 12px';
            popup.style.borderRadius = '10px';
            popup.style.border = '1px solid rgba(255,255,255,0.18)';
            popup.style.background = 'rgba(24,24,28,0.96)';
            popup.style.color = '#f5f5f5';
            popup.style.boxShadow = '0 14px 36px rgba(0,0,0,0.45)';
            popup.style.fontSize = '13px';
            popup.style.lineHeight = '1.35';
            popup.style.pointerEvents = 'none';
            popup.innerHTML = `
                <div style="font-weight:700;margin-bottom:6px;">Confirm Click</div>
                <div style="opacity:0.92;">Target: ${screenX}, ${screenY}</div>
                <div style="margin-top:6px;opacity:0.82;">Enter / Space = confirm</div>
                <div style="opacity:0.82;">Any other key = deny this click</div>
                <div style="opacity:0.82;">Esc = ${isLoopRunning ? 'stop loop' : 'deny'}</div>`;

            document.body.appendChild(popup);

            const rect = popup.getBoundingClientRect();
            let left = mouse.x + 18;
            let top = mouse.y + 18;
            if (left + rect.width > window.innerWidth - 8) {
                left = Math.max(8, mouse.x - rect.width - 18);
            }
            if (top + rect.height > window.innerHeight - 8) {
                top = Math.max(8, mouse.y - rect.height - 18);
            }
            popup.style.left = `${left}px`;
            popup.style.top = `${top}px`;

            let done = false;
            const finalize = function (result) {
                if (done) return;
                done = true;
                cleanup();
                resolve(result);
            };

            const onKeyDown = function (e) {
                e.preventDefault();
                e.stopPropagation();

                if (e.key === 'Enter' || e.key === ' ') {
                    finalize('confirm');
                    return;
                }

                if (e.key === 'Escape') {
                    finalize(isLoopRunning ? 'cancel-loop' : 'deny');
                    return;
                }

                finalize('deny');
            };

            const cleanup = function () {
                document.removeEventListener('keydown', onKeyDown, true);
                if (popup.parentElement) {
                    popup.remove();
                }
                if (window._sharpestClickerConfirmCleanup === cleanup) {
                    window._sharpestClickerConfirmCleanup = null;
                }
            };

            window._sharpestClickerConfirmCleanup = cleanup;
            document.addEventListener('keydown', onKeyDown, true);
        });
    }
};