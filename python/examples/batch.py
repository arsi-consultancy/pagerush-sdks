"""Render one PDF per CSV row in a batch and save the ZIP.

Each row becomes the template data for one PDF. With a "filename" column, that value names the PDF
inside the ZIP. Without a CSV file, three sample records are used.

Run: PAGERUSH_API_KEY=pk_live_... python examples/batch.py <template_id> [records.csv]
"""

import csv
import sys
from typing import Any, Dict, List, Optional

import pagerush


def load_items(csv_path: Optional[str]) -> List[Dict[str, Any]]:
    if csv_path is None:
        names = ["Ada", "Grace", "Linus"]
        return [{"data": {"name": name}, "filename": f"hello-{name.lower()}"} for name in names]
    items: List[Dict[str, Any]] = []
    with open(csv_path, newline="", encoding="utf-8") as f:
        for row in csv.DictReader(f):
            item: Dict[str, Any] = {"data": row}
            if row.get("filename"):
                item["filename"] = row["filename"]
            items.append(item)
    return items


def main(template_id: str, csv_path: Optional[str]) -> None:
    client = pagerush.PageRush()

    batch = client.batches.create(
        template_id,
        items=load_items(csv_path),
        metadata={"source": "batch.py"},
    )
    print(f"Queued batch {batch['id']} with {batch['item_count']} item(s)")

    batch = client.batches.wait(batch["id"], timeout=600, interval=2)
    print(f"Batch {batch['status']}: {batch['succeeded']} succeeded, {batch['failed']} failed")
    for item in batch.get("items", []):
        if item["error"]:
            print(f"  item {item['index']} failed: {item['error']['code']} {item['error']['message']}")

    if batch["succeeded"] == 0:
        sys.exit(1)
    data = client.batches.download_zip(batch["id"])
    with open("batch.zip", "wb") as f:
        f.write(data)
    print(f"Saved batch.zip ({len(data)} bytes)")


if __name__ == "__main__":
    if len(sys.argv) not in (2, 3):
        sys.exit("usage: python examples/batch.py <template_id> [records.csv]")
    main(sys.argv[1], sys.argv[2] if len(sys.argv) == 3 else None)
