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
};
