// Lightweight panning and Shift+Scroll support for the GuideGrid component.
// Register in index.html or _Host.cshtml: <script src="~/js/guide-pan.js"></script>
window.guide = {
    initPanning: function (bodyElement, dotNetRef) {
        if (!bodyElement) return;
        const el = bodyElement;
        if (el._guidePanHandlers) return;
        let isDown = false;
        let startX, scrollLeft;

        const onMouseDown = (e) => {
            if (e.button !== 0) return;
            isDown = true;
            el.classList.add('dragging');
            startX = e.pageX - el.offsetLeft;
            scrollLeft = el.scrollLeft;
            e.preventDefault();
        };

        const onMouseUp = () => {
            isDown = false;
            el.classList.remove('dragging');
        };

        const onMouseMove = (e) => {
            if (!isDown) return;
            const x = e.pageX - el.offsetLeft;
            const walk = (x - startX); // px moved
            el.scrollLeft = scrollLeft - walk;
        };

        // Shift + mouse wheel for horizontal
        const onWheel = (e) => {
            if (e.shiftKey) {
                el.scrollLeft += e.deltaY;
                e.preventDefault();
            }
        };
        el.addEventListener('mousedown', onMouseDown);
        document.addEventListener('mouseup', onMouseUp);
        document.addEventListener('mousemove', onMouseMove);
        el.addEventListener('wheel', onWheel, { passive: false });
        el._guidePanHandlers = { onMouseDown, onMouseUp, onMouseMove, onWheel };
    },

    disposePanning: function (bodyElement) {
        const el = bodyElement;
        const h = el?._guidePanHandlers;
        if (!el || !h) return;
        el.removeEventListener('mousedown', h.onMouseDown);
        document.removeEventListener('mouseup', h.onMouseUp);
        document.removeEventListener('mousemove', h.onMouseMove);
        el.removeEventListener('wheel', h.onWheel);
        delete el._guidePanHandlers;
    },

    scrollToNow: function (bodyElement, leftPx) {
        if (!bodyElement) return;
        // center now indicator
        const el = bodyElement;
        const rect = el.getBoundingClientRect();
        const center = rect.width / 2;
        const target = leftPx - center;
        el.scrollTo({ left: target, behavior: 'smooth' });
    }
};