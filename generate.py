import random

LINE_COUNT = 500_000_000
OUTPUT = "Test/very_big.txt"

WORDS = [
    "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit",
    "sed", "do", "eiusmod", "tempor", "incididunt", "ut", "labore", "et", "dolore",
    "magna", "aliqua", "enim", "ad", "minim", "veniam", "quis", "nostrud",
    "exercitation", "ullamco", "laboris", "nisi", "aliquip", "ex", "ea", "commodo",
    "consequat", "duis", "aute", "irure", "reprehenderit", "voluptate", "velit",
    "esse", "cillum", "fugiat", "nulla", "pariatur", "excepteur", "sint", "occaecat",
    "cupidatat", "non", "proident", "sunt", "culpa", "qui", "officia", "deserunt",
    "mollit", "anim", "id", "est", "laborum",
]

with open(OUTPUT, "w", encoding="utf-8") as f:
    for i in range(1, LINE_COUNT + 1):
        words = random.choices(WORDS, k=random.randint(5, 15))
        f.write(f"{i}: {' '.join(words)}\n")

print(f"Generated {LINE_COUNT} lines → {OUTPUT}")
