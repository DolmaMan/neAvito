// Frontend regression checks with intercepted API responses. No database or running server required.
// Run from repository root with Playwright and Edge (or PLAYWRIGHT_CHANNEL=chrome).
const { chromium } = require("playwright");
const fs = require("fs"),
    path = require("path"),
    assert = require("assert/strict");
const root = path.resolve("NeAvito/wwwroot");
const out = path.resolve("NeAvito/obj/interface-check");
fs.mkdirSync(out, { recursive: true });
(async () => {
    const origin = "http://neavito.test";
    const browser = await chromium.launch({
        channel: process.env.PLAYWRIGHT_CHANNEL || "msedge",
        headless: true,
    });
    const page = await browser.newPage();
    const errors = [];
    page.on("pageerror", (e) => errors.push(e.message));
    const id = "11111111-1111-1111-1111-111111111111";
    let loggedIn = false;
    let writes = [];
    let saveMode = "ok",
        releaseSave;
    let catalogMode = "ok";
    const profile = {
        id: "viewer",
        displayName: "Александр",
        email: "test@example.com",
        phone: "",
        deliveryAddress: "",
        version: "1",
    };
    const item = {
        id,
        title: "Шерстяное пальто прямого кроя",
        description: "Лаконичный силуэт. Отличное состояние.",
        price: 8500,
        cityName: "Москва",
        condition: "Used",
        sellerName: "Анна",
        category: { name: "Одежда" },
        city: { name: "Москва" },
        seller: { displayName: "Анна" },
        images: [{ id: "image1", url: "/fixture.png" }],
        status: "Draft",
        version: "v1",
        publicationErrors: [],
        phone: "",
        contactConsent: false,
        publishedAtUtc: "2026-11-10T10:00:00Z",
    };
    await page.route("**/*", (route) => {
        const url = new URL(route.request().url());
        if (url.pathname === "/fixture.png")
            return route.fulfill({
                contentType: "image/png",
                body: Buffer.from(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=",
                    "base64",
                ),
            });
        const file = path.join(root, url.pathname === "/" ? "index.html" : url.pathname);
        return route.fulfill({
            contentType: file.endsWith(".css")
                ? "text/css"
                : file.endsWith(".js")
                  ? "text/javascript"
                  : "text/html",
            body: fs.readFileSync(file),
        });
    });
    await page.route("**/api/**", async (route) => {
        const url = new URL(route.request().url());
        let data = {},
            status = 200;
        if (route.request().method() !== "GET") writes.push(url.pathname);
        if (url.pathname === "/api/me/listings" && route.request().method() === "POST") {
            if (saveMode === "delay")
                await new Promise((resolve) => {
                    releaseSave = resolve;
                });
            if (saveMode === "conflict") {
                status = 409;
                data = { message: "Объявление изменилось. Обновите страницу." };
            } else data = item;
        } else if (url.pathname === "/api/me/listings/" + id && route.request().method() === "GET")
            data = item;
        else if (url.pathname.endsWith("/images") && route.request().method() === "POST") data = item;
        else if (url.pathname === "/api/profile") {
            data = profile;
            if (!loggedIn) status = 401;
        } else if (url.pathname.endsWith("/categories"))
            data = [
                { id: 1, name: "Одежда" },
                { id: 2, name: "Обувь" },
                { id: 3, name: "Дом и быт" },
                { id: 4, name: "Электроника" },
            ];
        else if (url.pathname.endsWith("/cities"))
            data = [
                { id: 1, name: "Москва" },
                { id: 2, name: "Иркутск" },
            ];
        else if (url.pathname === "/api/me/listings") data = { items: [], page: 1, totalPages: 1 };
        else if (url.pathname === "/api/listings" && catalogMode === "offline") return route.abort();
        else if (url.pathname === "/api/listings" && catalogMode === "empty")
            data = { items: [], totalCount: 0, page: 1, totalPages: 1 };
        else if (url.pathname === "/api/listings")
            data = {
                items: Array.from({ length: 8 }, (_, i) => ({
                    ...item,
                    title: [
                        "Шерстяное пальто",
                        "Кожаные лоферы",
                        "Настольная лампа",
                        "ОченьДлинноеНазваниеБезПробелов".repeat(5),
                    ][i % 4],
                })),
                totalCount: 8,
                page: 1,
                totalPages: 1,
            };
        else if (url.pathname === "/api/listings/" + id) data = item;
        else if (url.pathname.endsWith("/contact")) data = { phone: "+79991234567" };
        else if (url.pathname === "/api/auth/login") loggedIn = true;
        else if (url.pathname === "/api/auth/logout") loggedIn = false;
        else throw Error("Unexpected API " + url.pathname);
        return route.fulfill({ status, contentType: "application/json", body: JSON.stringify(data) });
    });
    try {
        for (const width of [390, 768, 1440]) {
            await page.setViewportSize({ width, height: 1000 });
            await page.goto(origin);
            await page.locator(".shop-card").first().waitFor();
            assert(
                await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
                "catalog overflow " + width,
            );
            await page.screenshot({ path: path.join(out, "catalog-" + width + ".png"), fullPage: true });
            await page.locator(".shop-card h2 a").first().click();
            await page.locator("#reveal-contact").waitFor();
            assert(
                await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
                "detail overflow " + width,
            );
            await page.getByRole("button", { name: "Пожаловаться на объявление" }).click();
            await page.locator("#auth-form").waitFor();
            assert.equal(new URL(page.url()).hash, "#login");
            await page.goto(origin + "/?auth-check#login");
            await page.locator("#auth-form").waitFor();
            assert(
                await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
                "auth overflow " + width,
            );
            await page.screenshot({ path: path.join(out, "auth-" + width + ".png"), fullPage: true });
        }
        await page.locator("[name=email]").fill("test@example.com");
        await page.locator("#auth-form [name=password]").fill("Password123!");
        await page.locator("#auth-submit").click();
        await page.locator("#profile-form").waitFor();
        for (const width of [390, 768, 1440]) {
            await page.setViewportSize({ width, height: 1000 });
            for (const route of ["profile", "listings", "listing"]) {
                await page.goto(origin + "/#" + route);
                await page.locator("#content h1").waitFor();
                assert(
                    await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
                    route + " overflow " + width,
                );
                await page.screenshot({ path: path.join(out, route + "-" + width + ".png"), fullPage: true });
            }
        }
        await page.goto(origin + "/#moderation");
        await page.getByRole("heading", { name: "Нет доступа" }).waitFor();
        assert.equal(await page.locator('a[href="#moderation"]').isVisible(), false);
        // Regression: a new unsaved draft must prompt on the in-content back link.
        await page.goto(origin + "/#listing");
        await page.locator("#listing-form").waitFor();
        await page.locator("[name=title]").fill("Несохранённый черновик");
        page.once("dialog", (dialog) => dialog.dismiss());
        await page.locator("#content > a").click();
        assert.equal(new URL(page.url()).hash, "#listing");
        page.once("dialog", (dialog) => dialog.accept());
        await page.locator("#content > a").click();
        await page.waitForURL("**/#listings");
        // Regression: saving locks the editor and a late response must not navigate back.
        await page.goto(origin + "/#listing");
        await page.locator("#listing-form").waitFor();
        await page.locator("[name=title]").fill("Сохранение");
        saveMode = "delay";
        await page.locator("#listing-form button[type=submit]").click();
        await page.waitForFunction(() => document.querySelector("#listing-form input").disabled);
        assert(await page.locator("#listing-form button[type=submit]").isDisabled());
        page.once("dialog", (dialog) => dialog.accept());
        await page.locator('.cabinet nav a[href="#profile"]').click();
        await page.locator("#profile-form").waitFor();
        releaseSave();
        await page.waitForFunction(() => !listingBusy);
        assert.equal(new URL(page.url()).hash, "#profile");
        // Conflict keeps input and restores controls.
        saveMode = "conflict";
        await page.goto(origin + "/#listing");
        await page.locator("#listing-form").waitFor();
        await page.locator("[name=title]").fill("Мой текст");
        await page.locator("#listing-form button[type=submit]").click();
        await page.locator("#listing-error").filter({ hasText: "Объявление изменилось" }).waitFor();
        assert.equal(await page.locator("[name=title]").inputValue(), "Мой текст");
        assert(await page.locator("[name=title]").isEnabled());
        // Upload validation must prevent an unsupported file from reaching the API.
        await page.goto(origin + "/#listing?id=" + id);
        await page.locator("#listing-photo").waitFor();
        await page.waitForFunction(() => document.querySelector("#listing-images img").naturalWidth > 0);
        const before = writes.length;
        await page
            .locator("#listing-photo")
            .setInputFiles({ name: "bad.txt", mimeType: "text/plain", buffer: Buffer.from("bad") });
        await page.locator("#notice").filter({ hasText: "JPEG" }).waitFor();
        assert.equal(writes.length, before);
        // Empty catalogue and failed requests remain navigable.
        catalogMode = "empty";
        await page.goto(origin);
        await page.getByRole("heading", { name: "Объявления не найдены" }).waitFor();
        catalogMode = "offline";
        await page.goto(origin);
        await page.locator("#catalog-error").waitFor();
        catalogMode = "ok";
        await page.getByRole("button", { name: "Повторить", exact: true }).click();
        await page.locator(".shop-card").first().waitFor();
        assert(!writes.some((p) => p.includes("moderation") || p.includes("reports")));
        assert.deepEqual(errors, []);
        console.log(
            "PASS: draft guard, stale save, duplicate protection, conflict recovery, upload validation, empty/offline retry; 390/768/1440 catalogue, detail, auth, profile, listings, editor; login; guest report redirect and moderation access denied; no page errors.",
        );
    } finally {
        await browser.close();
    }
})().catch((e) => {
    console.error(e);
    process.exitCode = 1;
});
