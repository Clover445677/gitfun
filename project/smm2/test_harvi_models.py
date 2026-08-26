import json
import os
from dataclasses import dataclass

from dotenv import load_dotenv
from openai import OpenAI, OpenAIError


@dataclass(frozen=True)
class ModelCandidate:
    provider: str
    name: str


MODELS = [
    ModelCandidate("CLAUDE", "Opus 5"),
    ModelCandidate("CLAUDE", "Opus 4.8"),
    ModelCandidate("CLAUDE", "Sonnet 4.6"),
    ModelCandidate("CLAUDE", "Sonnet 4.5"),
    ModelCandidate("CLAUDE", "Haiku 4.5"),
    ModelCandidate("GPT", "GPT-5.6 Terra MEDIUM"),
    ModelCandidate("GPT", "GPT-5.5 MEDIUM"),
    ModelCandidate("GPT", "GPT-5.6 Luna MEDIUM"),
    ModelCandidate("GPT", "GPT-5.4 Mini HIGH"),
    ModelCandidate("GPT", "GPT-5.4 Mini LOW"),
    ModelCandidate("GEMINI", "Gemini 3.1 Pro"),
    ModelCandidate("HARVI", "Harvi Ultra 2"),
    ModelCandidate("HARVI", "Harvi Sonic FAST"),
]


def model_variants(name: str) -> list[str]:
    normalized = name.lower().replace(".", "-").replace(" ", "-")
    compact = name.lower().replace(" ", "")
    return list(dict.fromkeys([name, normalized, compact]))


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


def check_model(client: OpenAI, model: str) -> bool:
    response = client.chat.completions.create(
        model=model,
        messages=[{"role": "user", "content": "Напиши 'ОК'"}],
        max_tokens=20,
    )
    text = extract_text(response).strip()
    print(f"Ответ: {text}")
    return bool(text)


def main() -> None:
    load_dotenv()
    api_key = os.getenv("OPENAI_API_KEY")
    base_url = os.getenv("OPENAI_BASE_URL", "https://api.harvi.pro/v1")

    if not api_key:
        raise SystemExit("Ошибка: задайте OPENAI_API_KEY в файле .env")

    client = OpenAI(api_key=api_key, base_url=base_url)

    for candidate in MODELS:
        for model in model_variants(candidate.name):
            print(f"Проверяю {candidate.provider}: {model}")
            try:
                if check_model(client, model):
                    print(f"РАБОЧАЯ_МОДЕЛЬ={model}")
                    return
            except (OpenAIError, RuntimeError, json.JSONDecodeError) as error:
                print(f"Ошибка: {error}")

    print("Ни одна модель из списка не сработала. Альтернатива: подключить KeylessAI или уточнить у Harvi точные API-id моделей для вашего тарифа.")


if __name__ == "__main__":
    main()
