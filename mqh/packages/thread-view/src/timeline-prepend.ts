// Moved from upstream's @bb/client-core (a web-client package MQH removes): joins an older timeline page
// in front of the rows already loaded, without duplicates, merging the children of rows present in both.
// Used by `mqh thread show` when it pages back through history.
import type { TimelineRow } from "@bb/server-contract";

export interface PrependOlderTimelineRowsArgs {
  loadedRows: readonly TimelineRow[];
  olderRows: readonly TimelineRow[];
}

function appendTimelineRowsPreservingOrder(
  target: TimelineRow[],
  rows: readonly TimelineRow[],
): void {
  const seenIds = new Set(target.map((row) => row.id));
  for (const row of rows) {
    if (seenIds.has(row.id)) {
      continue;
    }
    seenIds.add(row.id);
    target.push(row);
  }
}

function areTimelineRowReferencesEqual(
  left: readonly TimelineRow[],
  right: readonly TimelineRow[],
): boolean {
  if (left.length !== right.length) return false;
  return left.every((row, index) => row === right[index]);
}

function joinOlderTimelineRowChildren(
  older: TimelineRow,
  loaded: TimelineRow,
): TimelineRow {
  if (
    older.kind === "turn" &&
    loaded.kind === "turn" &&
    older.children !== null &&
    loaded.children !== null
  ) {
    const children = prependOlderTimelineRows({
      olderRows: older.children,
      loadedRows: loaded.children,
    });
    if (areTimelineRowReferencesEqual(children, loaded.children)) {
      return loaded;
    }
    return { ...loaded, children };
  }
  if (
    older.kind === "work" &&
    older.workKind === "delegation" &&
    loaded.kind === "work" &&
    loaded.workKind === "delegation" &&
    older.childRows !== null &&
    loaded.childRows !== null
  ) {
    const childRows = prependOlderTimelineRows({
      olderRows: older.childRows,
      loadedRows: loaded.childRows,
    });
    if (areTimelineRowReferencesEqual(childRows, loaded.childRows)) {
      return loaded;
    }
    return { ...loaded, childRows };
  }
  return loaded;
}

export function prependOlderTimelineRows({
  loadedRows,
  olderRows,
}: PrependOlderTimelineRowsArgs): TimelineRow[] {
  const uniqueLoadedRows: TimelineRow[] = [];
  appendTimelineRowsPreservingOrder(uniqueLoadedRows, loadedRows);
  const loadedIds = new Set(uniqueLoadedRows.map((row) => row.id));
  const olderById = new Map<string, TimelineRow>();
  const rows: TimelineRow[] = [];
  for (const row of olderRows) {
    if (olderById.has(row.id)) {
      continue;
    }
    olderById.set(row.id, row);
    if (!loadedIds.has(row.id)) {
      rows.push(row);
    }
  }
  for (const loaded of uniqueLoadedRows) {
    const older = olderById.get(loaded.id);
    rows.push(
      older === undefined
        ? loaded
        : joinOlderTimelineRowChildren(older, loaded),
    );
  }
  return rows;
}
