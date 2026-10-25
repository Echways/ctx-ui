window.ctxUi = {
    visible: () => document.visibilityState === "visible",
    firstVisit: key => {
        try {
            if (sessionStorage.getItem(key)) {
                return false;
            }
            sessionStorage.setItem(key, "1");
            return true;
        } catch {
            return false;
        }
    },
    followMark: nav => {
        const mark = nav.querySelector(".mark");
        const place = () => {
            const link = nav.querySelector("a.active");
            mark.classList.toggle("placed", Boolean(link));
            if (!link) {
                return;
            }
            mark.style.setProperty("--x", `${link.offsetLeft}px`);
            mark.style.setProperty("--y", `${link.offsetTop}px`);
            mark.style.setProperty("--w", `${link.offsetWidth}px`);
            mark.style.setProperty("--h", `${link.offsetHeight}px`);
        };
        place();
        requestAnimationFrame(() => mark.classList.add("moving"));
        new MutationObserver(place).observe(nav, { subtree: true, attributeFilter: ["class"] });
        new ResizeObserver(place).observe(nav);
        nav.addEventListener("transitionend", place);
        document.fonts.ready.then(place);
    },
};
