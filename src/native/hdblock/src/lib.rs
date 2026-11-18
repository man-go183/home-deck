//! hdblock — C ABI wrapper around Brave's `adblock-rust` engine for Home Deck.
//!
//! The C# side (`RustFilterEngine`) owns exactly one engine handle per
//! process: create it, load concatenated filter-list text into it, then query
//! per request. Every exported function is infallible across the boundary —
//! any error (bad input, no engine, internal panic) returns "allow" (0) or
//! null, so blocking can only ever fail open, never crash the app.
//!
//! Request-type codes (must match `RustFilterEngine.TypeCode` in C#):
//! 0 = other, 1 = script, 2 = image, 3 = stylesheet, 4 = xmlhttprequest,
//! 5 = subdocument, 6 = font, 7 = media, 8 = websocket.
//! Top-level document/main-frame loads are never sent here (the C# side
//! skips them) — only subresources.

use adblock::engine::Engine;
use adblock::request::Request;

use std::ffi::CString;
use std::os::raw::{c_char, c_int};
use std::panic;
use std::ptr;
use std::slice;

fn read_str(ptr: *const c_char, len: usize) -> Option<String> {
    if ptr.is_null() {
        return None;
    }
    let bytes = unsafe { slice::from_raw_parts(ptr as *const u8, len) };
    String::from_utf8(bytes.to_vec()).ok()
}

fn request_type_name(t: c_int) -> &'static str {
    match t {
        1 => "script",
        2 => "image",
        3 => "stylesheet",
        4 => "xmlhttprequest",
        5 => "sub_frame",
        6 => "font",
        7 => "media",
        8 => "websocket",
        _ => "other",
    }
}

/// ABI contract version. The C# side refuses to use any other version.
#[no_mangle]
pub extern "C" fn hdblock_version() -> c_int {
    1
}

/// Create an empty engine. Returns null on failure. Free with `hdblock_destroy`.
#[no_mangle]
pub extern "C" fn hdblock_create() -> *mut Engine {
    match panic::catch_unwind(|| Box::new(Engine::new_with_list_text(String::new()))) {
        Ok(b) => Box::into_raw(b),
        Err(_) => ptr::null_mut(),
    }
}

/// Parse concatenated filter-list text (UTF-8, `rules_len` bytes) and swap it
/// into the engine, replacing any previous rules. Returns 1 on success.
#[no_mangle]
pub extern "C" fn hdblock_load_rules(
    handle: *mut Engine,
    rules_ptr: *const c_char,
    rules_len: usize,
) -> c_int {
    if handle.is_null() {
        return 0;
    }
    let result = panic::catch_unwind(|| {
        let text = read_str(rules_ptr, rules_len).ok_or(())?;
        let engine = Engine::new_with_list_text(text);
        unsafe {
            *handle = engine;
        }
        Ok::<(), ()>(())
    });
    match result {
        Ok(Ok(())) => 1,
        _ => 0,
    }
}

/// 1 = block, 0 = allow. `page` is the top-level page host (` "" ` if unknown,
/// treated as third-party). Never blocks on any error.
#[no_mangle]
pub extern "C" fn hdblock_should_block(
    handle: *const Engine,
    url_ptr: *const c_char,
    url_len: usize,
    page_ptr: *const c_char,
    page_len: usize,
    req_type: c_int,
) -> c_int {
    let result = panic::catch_unwind(|| {
        let engine = unsafe { handle.as_ref() }.ok_or(())?;
        let url = read_str(url_ptr, url_len).ok_or(())?;
        let host = read_str(page_ptr, page_len).unwrap_or_default();
        let source = if host.is_empty() {
            String::new()
        } else {
            format!("https://{host}/")
        };
        let req = Request::new(&url, &source, request_type_name(req_type), "").map_err(|_| ())?;
        Ok::<bool, ()>(engine.check_network_request(&req).should_block())
    });
    match result {
        Ok(Ok(true)) => 1,
        _ => 0,
    }
}

/// Element-hide CSS for a page URL (`{display:none!important}`), or null when
/// there is nothing to hide. Free with `hdblock_free_string`. Scriptlets and
/// procedural filters are intentionally not executed (phase 1).
#[no_mangle]
pub extern "C" fn hdblock_cosmetic_css(
    handle: *const Engine,
    url_ptr: *const c_char,
    url_len: usize,
) -> *mut c_char {
    let result = panic::catch_unwind(|| {
        let engine = unsafe { handle.as_ref() }.ok_or(())?;
        let url = read_str(url_ptr, url_len).ok_or(())?;
        let res = engine.url_cosmetic_resources(&url);
        if res.hide_selectors.is_empty() {
            return Err(());
        }
        let mut sels: Vec<&str> = res.hide_selectors.iter().map(|s| s.as_str()).collect();
        sels.sort_unstable();
        Ok::<String, ()>(format!("{}{{display:none!important}}", sels.join(",")))
    });
    match result {
        Ok(Ok(css)) => match CString::new(css) {
            Ok(c) => c.into_raw(),
            Err(_) => ptr::null_mut(),
        },
        _ => ptr::null_mut(),
    }
}

/// Free a string returned by `hdblock_cosmetic_css`. Null-safe.
#[no_mangle]
pub extern "C" fn hdblock_free_string(s: *mut c_char) {
    if !s.is_null() {
        let _ = unsafe { CString::from_raw(s) };
    }
}

/// Destroy an engine created by `hdblock_create`. Null-safe.
#[no_mangle]
pub extern "C" fn hdblock_destroy(handle: *mut Engine) {
    if !handle.is_null() {
        let _ = unsafe { Box::from_raw(handle) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const LISTS: &str = "\
||ads.example.com^\n\
@@||ads.example.com/allowed^\n\
||cdn.example.com^$third-party\n\
||img.example.com^$image\n\
##.ad-banner\n";

    fn engine() -> Engine {
        Engine::new_with_list_text(LISTS.to_string())
    }

    fn req(url: &str, source: &str, t: &str) -> Request {
        Request::new(url, source, t, "").expect("test request must parse")
    }

    #[test]
    fn blocks_matching_third_party_script() {
        let e = engine();
        let r = req(
            "https://ads.example.com/b.js",
            "https://news.example.com/",
            "script",
        );
        assert!(e.check_network_request(&r).should_block());
    }

    #[test]
    fn exception_allows() {
        let e = engine();
        let r = req(
            "https://ads.example.com/allowed/logo.png",
            "https://news.example.com/",
            "image",
        );
        assert!(!e.check_network_request(&r).should_block());
    }

    #[test]
    fn third_party_option_respected() {
        let e = engine();
        // same site: first-party, $third-party rule must not match
        let first = req(
            "https://cdn.example.com/app.js",
            "https://cdn.example.com/",
            "script",
        );
        assert!(!e.check_network_request(&first).should_block());
        // other site: third-party, must match
        let third = req(
            "https://cdn.example.com/app.js",
            "https://news.example.com/",
            "script",
        );
        assert!(e.check_network_request(&third).should_block());
    }

    #[test]
    fn resource_type_scoping() {
        let e = engine();
        let img = req(
            "https://img.example.com/p.png",
            "https://news.example.com/",
            "image",
        );
        assert!(e.check_network_request(&img).should_block());
        let js = req(
            "https://img.example.com/p.png",
            "https://news.example.com/",
            "script",
        );
        assert!(!e.check_network_request(&js).should_block());
    }

    #[test]
    fn cosmetic_selectors_returned() {
        let e = engine();
        let res = e.url_cosmetic_resources("https://news.example.com/article");
        assert!(res.hide_selectors.contains(".ad-banner"));
    }

    #[test]
    fn c_abi_roundtrip() {
        unsafe {
            let h = hdblock_create();
            assert!(!h.is_null());
            let text = CString::new(LISTS).unwrap();
            assert_eq!(
                hdblock_load_rules(h, text.as_ptr(), LISTS.len() as usize),
                1
            );
            let url = CString::new("https://ads.example.com/b.js").unwrap();
            let page = CString::new("news.example.com").unwrap();
            assert_eq!(
                hdblock_should_block(
                    h,
                    url.as_ptr(),
                    "https://ads.example.com/b.js".len(),
                    page.as_ptr(),
                    "news.example.com".len(),
                    1,
                ),
                1
            );
            let page_url = CString::new("https://news.example.com/article").unwrap();
            let css = hdblock_cosmetic_css(
                h,
                page_url.as_ptr(),
                "https://news.example.com/article".len(),
            );
            assert!(!css.is_null());
            let s = CStr::from_ptr(css).to_string_lossy().into_owned();
            assert!(s.contains(".ad-banner"));
            hdblock_free_string(css);
            hdblock_destroy(h);
        }
    }
}
