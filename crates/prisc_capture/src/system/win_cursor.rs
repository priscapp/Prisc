use crate::system::{CapturedCursor, CursorImage};

pub fn capture_cursor() -> Option<CapturedCursor> {
    let Some(cursor) = prisc_desktop::cursor_snapshot() else {
        warn!("GetCursorInfo failed");
        return None;
    };

    if !cursor.showing || cursor.handle == 0 {
        return Some(CapturedCursor {
            position: cursor.position,
            hotspot_x: 0,
            hotspot_y: 0,
            visible: false,
            image: CursorImage::AlphaBlended {
                bgra: Vec::new(),
                width: 0,
                height: 0,
            },
        });
    }

    let Some(icon) = prisc_desktop::read_cursor(cursor.handle) else {
        warn!("could not read the cursor bitmap");
        return None;
    };
    let image = CursorImage::from_icon(icon.width, icon.height, icon.pixels)?;

    Some(CapturedCursor {
        position: cursor.position,
        hotspot_x: icon.hotspot_x,
        hotspot_y: icon.hotspot_y,
        visible: true,
        image,
    })
}
