"""Create a template, render it with data and save invoice.pdf.

Run: PAGERUSH_API_KEY=pk_live_... python examples/quickstart.py
"""

import pagerush

SOURCE = """#let data = json("data.json")
= Invoice #data.number

Billed to: #data.customer

#table(
  columns: (1fr, auto),
  [*Item*], [*Amount*],
  ..data.items.map(item => (item.name, item.amount)).flatten(),
)
"""


def main() -> None:
    client = pagerush.PageRush()  # reads PAGERUSH_API_KEY

    created = client.templates.create(name="Invoice", source=SOURCE)
    template_id = created["template"]["id"]
    print("Created template", template_id)

    result = client.templates.render(
        template_id,
        data={
            "number": "INV-001",
            "customer": "Example Ltd",
            "items": [{"name": "Design", "amount": "$400"}, {"name": "Hosting", "amount": "$20"}],
        },
        filename="invoice-001",
    )
    result.save("invoice.pdf")
    size = len(result.pdf or b"")
    print(f"Saved invoice.pdf ({size} bytes, {result.page_count} page(s), job {result.job_id})")


if __name__ == "__main__":
    main()
