(function () {
    "use strict";

    const unsafeMethods = new Set(["POST", "PUT", "PATCH", "DELETE"]);
    const antiforgeryHeader = "RequestVerificationToken";
    const originalFetch = window.fetch.bind(window);

    function getRequestToken() {
        return document.querySelector('input[name="__RequestVerificationToken"]')?.value || "";
    }

    window.fetch = function (input, init = {}) {
        const sourceRequest = input instanceof Request ? input : null;
        const method = String(init.method || sourceRequest?.method || "GET").toUpperCase();
        const requestUrl = new URL(sourceRequest?.url || input, window.location.href);

        if (requestUrl.origin !== window.location.origin || !unsafeMethods.has(method)) {
            return originalFetch(input, init);
        }

        const token = getRequestToken();
        if (!token) {
            return Promise.reject(new Error("The page antiforgery token is unavailable."));
        }

        const headers = new Headers(sourceRequest?.headers);
        new Headers(init.headers).forEach((value, name) => headers.set(name, value));
        headers.set(antiforgeryHeader, token);

        return originalFetch(input, {
            ...init,
            headers,
            credentials: init.credentials || sourceRequest?.credentials || "same-origin"
        });
    };
})();
