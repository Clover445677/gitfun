import os
import json
from openai import OpenAI
from prompts import build_prompt  # Берём промпт из вашего проекта

# Загружаем .env
from dotenv import load_dotenv
load_dotenv()

# Клиент для Ollama
client = OpenAI(
    base_url="http://localhost:11434/v1",
    api_key="ollama"
)

# Тестовые данные
test_data = {
    "theme": "Как малому бизнесу привлечь клиентов через Telegram",
    "audience": "Владельцы локальных сервисов",
    "gender": "Неважно",
    "age": "25-45",
    "pain": "Не понимаю, как привлечь клиентов без бюджета",
    "platform": "Telegram",
    "tone": "Экспертный",
    "format": "Карусель",
    "prompt": ""
}

# Строим промпт из prompts.py
prompt = build_prompt(test_data)

print("📤 Отправка запроса к модели...")
print(f"Длина промпта: {len(prompt)} символов")

try:
    response = client.chat.completions.create(
        model="llama3.2:3b",
        messages=[
            {"role": "system", "content": "Ты — профессиональный SMM-продюсер. Отвечай только JSON."},
            {"role": "user", "content": prompt}
        ],
        temperature=0.8,
        max_tokens=4000
    )
    
    raw = response.choices[0].message.content
    print(f"✅ Ответ получен, длина: {len(raw)} символов")
    print("\n--- Сырой ответ ---")
    print(raw[:500] + "..." if len(raw) > 500 else raw)
    print("\n---")
    
    # Пробуем распарсить JSON
    try:
        data = json.loads(raw)
        print("✅ JSON успешно распарсен")
        print(f"Ключи: {list(data.keys())}")
    except json.JSONDecodeError as e:
        print(f"❌ Ошибка парсинга JSON: {e}")
        print("Попытка очистки через clean_json_response...")
        
        # Используем вашу функцию, если она есть
        try:
            from main import clean_json_response
            cleaned = clean_json_response(raw)
            if cleaned:
                data = json.loads(cleaned)
                print("✅ После очистки JSON распарсен")
            else:
                print("❌ clean_json_response вернул None")
        except:
            print("⚠️ Функция clean_json_response не найдена")
            
except Exception as e:
    print(f"❌ Ошибка: {e}")