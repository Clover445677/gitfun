import os
import json

from dotenv import load_dotenv
from openai import OpenAI, OpenAIError


def extract_text(response: object) -> str:
    if not isinstance(response, str):
        return response.choices[0].message.content or ""

    content_parts: list[str] = []
    for line in response.splitlines():
        if not line.startswith("data:"):
            continue
        payload = line.removeprefix("data:").strip()
        if not payload or payload == "[DONE]":
            continue
        data = json.loads(payload)
        if "error" in data:
            raise RuntimeError(data["error"])
        for choice in data.get("choices", []):
            message = choice.get("message", {})
            delta = choice.get("delta", {})
            text = message.get("content") or delta.get("content")
            if text:
                content_parts.append(text)
    return "".join(content_parts)


def main() -> None:
    load_dotenv()
    api_key = os.getenv("OPENAI_API_KEY")
    base_url = os.getenv("OPENAI_BASE_URL", "https://api.harvi.pro/v1")
    model = os.getenv("OPENAI_MODEL", "gpt-4o-mini")

    if not api_key:
        raise SystemExit("Ошибка: задайте OPENAI_API_KEY в файле .env")

    client = OpenAI(api_key=api_key, base_url=base_url)
    try:
        response = client.chat.completions.create(
            model=model,
            messages=[{"role": "user", "content": "Привет"}],
            max_tokens=50,
        )
        print(extract_text(response))
    except (OpenAIError, RuntimeError, json.JSONDecodeError) as error:
        print(f"Ошибка подключения к Harvi: {error}")


if __name__ == "__main__":
    main()
