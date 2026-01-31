// Lightweight panning and Shift+Scroll support for the GuideGrid component.
// Register in index.html or _Host.cshtml: <script src="~/js/guide-pan.js"></script>
window.guide = {
    initPanning: function (bodyElement, dotNetRef) {
        if (!bodyElement) return;
        const el = bodyElement;
        let isDown = false;
        let startX, scrollLeft;

        el.addEventListener('mousedown', (e) => {
            if (e.button !== 0) return;
            isDown = true;
            el.classList.add('dragging');
            startX = e.pageX - el.offsetLeft;
            scrollLeft = el.scrollLeft;
            e.preventDefault();
        });

        document.addEventListener('mouseup', () => {
            isDown = false;
            el.classList.remove('dragging');
        });

        document.addEventListener('mousemove', (e) => {
            if (!isDown) return;
            const x = e.pageX - el.offsetLeft;
            const walk = (x - startX); // px moved
            el.scrollLeft = scrollLeft - walk;
        });

        // Shift + mouse wheel for horizontal
        el.addEventListener('wheel', (e) => {
            if (e.shiftKey) {
                el.scrollLeft += e.deltaY;
                e.preventDefault();
            }
        }, { passive: false });
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