"""Regenerate synthetic static text fixtures; requires Pillow, not needed to run probes."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

for name, text in [("video-ocr-static", "OUTSIDE VIDEO\nOCR STILL WORKS\nStable reading text"),
                   ("video-ocr-auto", "OUTSIDE VIDEO\nAUTO MASK WORKS\nStable reading text")]:
    image = Image.new("RGB", (820, 360), "white")
    draw = ImageDraw.Draw(image)
    draw.rectangle((0, 0, 399, 359), fill="black")
    draw.text((20, 20), "HIDDEN VIDEO TEXT", font=ImageFont.truetype("C:/Windows/Fonts/arial.ttf", 24), fill="white")
    draw.multiline_text((430, 30), text, font=ImageFont.truetype("C:/Windows/Fonts/arial.ttf", 28), fill="black", spacing=8)
    image.save(Path(__file__).parent / f"{name}.png")
