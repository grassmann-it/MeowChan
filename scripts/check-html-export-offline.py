#!/usr/bin/env python3
"""Verify a relocated thread export in an isolated Chromium with networking disabled.

Requires Playwright for Python and an existing Chromium executable. No browser profile,
server, provider, MeowChan process or network connection is used to read the export.
"""
import argparse
import json
from pathlib import Path
from playwright.sync_api import sync_playwright


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--chromium", required=True)
    parser.add_argument("--expect-posts", type=int, default=2)
    parser.add_argument("--expect-images", type=int, default=1)
    args = parser.parse_args()
    folder = args.directory.resolve(strict=True)
    index = folder / "index.html"
    args.output.mkdir(parents=True, exist_ok=True)
    requests, errors, consoles = [], [], []
    with sync_playwright() as driver:
        browser = driver.chromium.launch(executable_path=args.chromium, headless=True,
            args=["--disable-background-networking", "--disable-component-update", "--no-first-run"])
        context = browser.new_context(offline=True, viewport={"width": 1100, "height": 900})
        page = context.new_page()
        page.on("request", lambda request: requests.append(request.url))
        page.on("pageerror", lambda error: errors.append(str(error)))
        page.on("console", lambda message: consoles.append(message.text) if message.type == "error" else None)
        page.goto(index.as_uri(), wait_until="load")
        assert page.locator("main article[id]").count() == args.expect_posts, "Incorrect captured post count"
        assert page.locator("script,iframe,object,embed").count() == 0, "Active source content leaked"
        assert page.locator("[onclick],[onerror],[onload]").count() == 0, "Source event handler leaked"
        images = page.locator("img")
        assert images.count() == args.expect_images, "Incorrect saved image count"
        for image in images.all():
            image.scroll_into_view_if_needed()
            image.evaluate("image => image.decode()")
            assert image.evaluate("image => image.naturalWidth > 0 && image.naturalHeight > 0"), "Copied image failed to decode"
        quotes = page.locator("a.quote")
        assert quotes.count() > 0, "No internal captured quote navigation"
        target = quotes.first.get_attribute("href")
        assert target.startswith("#") and page.locator(target).count() == 1, "Broken local quote target"
        quotes.first.click()
        assert page.url.endswith(target), "Quote did not navigate within the file"
        assert page.locator("article").first.evaluate("element => getComputedStyle(element).backgroundColor") == "rgb(32, 36, 42)", "Own CSS did not load offline"
        for label in ("Post not captured", "Other thread, outside this export", "External reference", "Removed at source"):
            assert label in page.locator("body").inner_text(), f"Missing visible explanation: {label}"
        assert not errors and not consoles, f"Browser errors: {errors + consoles}"
        for url in requests:
            assert url.startswith(folder.as_uri() + "/"), f"Export requested a resource outside its relocated folder: {url}"
        page.screenshot(path=str(args.output / "offline-thread.png"), full_page=True)
        report = {"passed": True, "networking": "disabled", "index": str(index), "posts": args.expect_posts,
            "decoded_images": args.expect_images, "local_quote_target": target,
            "requests": requests, "page_errors": errors, "console_errors": consoles}
        (args.output / "result.json").write_text(json.dumps(report, indent=2) + "\n")
        print(json.dumps(report, indent=2))
        browser.close()


if __name__ == "__main__":
    main()
