from datetime import datetime
from typing import Any

from sqlalchemy import DateTime, ForeignKey, Integer, JSON, String, Text, UniqueConstraint, create_engine, inspect, text
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column, sessionmaker


class Base(DeclarativeBase):
    pass


class Generation(Base):
    __tablename__ = "generations"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow, nullable=False)
    theme: Mapped[str] = mapped_column(String(500), nullable=False)
    platform: Mapped[str] = mapped_column(String(50), nullable=False)
    input_data: Mapped[dict] = mapped_column(JSON, nullable=False)
    result: Mapped[dict] = mapped_column(JSON, nullable=False)
    current_version_id: Mapped[int] = mapped_column(Integer, nullable=True)


class Version(Base):
    __tablename__ = "versions"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    generation_id: Mapped[int] = mapped_column(Integer, nullable=False, index=True)
    version_number: Mapped[int] = mapped_column(Integer, nullable=False)
    copywriter_brief: Mapped[dict] = mapped_column(JSON, nullable=False)
    designer_brief: Mapped[dict] = mapped_column(JSON, nullable=False)
    is_active: Mapped[bool] = mapped_column(default=True, nullable=False)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow, nullable=False)
    user_prompt: Mapped[str] = mapped_column(String(3000), default="", nullable=False)
    parent_version_id: Mapped[int] = mapped_column(Integer, nullable=True)


class PostPlan(Base):
    __tablename__ = "post_plan"
    __table_args__ = (UniqueConstraint("sheet_url", "sheet_name", "row_index", name="uq_post_plan_sheet_row"),)

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    sheet_url: Mapped[str] = mapped_column(String(500), nullable=False)
    sheet_name: Mapped[str] = mapped_column(String(200), nullable=False, default="Сентябрь")
    row_index: Mapped[int] = mapped_column(Integer, nullable=False)
    publish_date: Mapped[datetime] = mapped_column(DateTime, nullable=False)
    theme: Mapped[str] = mapped_column(String(500), nullable=False)
    rubric: Mapped[str] = mapped_column(String(200), nullable=False, default="")
    description: Mapped[str] = mapped_column(Text, nullable=False, default="")
    idea: Mapped[str] = mapped_column(Text, nullable=True)
    status: Mapped[str] = mapped_column(String(100), nullable=False, default="")
    client_comment: Mapped[str] = mapped_column(Text, nullable=True)
    brief_id: Mapped[int] = mapped_column(ForeignKey("generations.id"), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow, nullable=False)
    updated_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow, onupdate=datetime.utcnow, nullable=False)


class PromptSettings(Base):
    __tablename__ = "prompt_settings"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    name: Mapped[str] = mapped_column(String(100), unique=True, nullable=False)
    # Единый JSON хранит текстовые настройки, пример ТЗ и состояния enabled полей.
    content: Mapped[dict[str, Any]] = mapped_column(JSON, nullable=False)
    updated_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow, onupdate=datetime.utcnow, nullable=False)


def create_database(database_url: str):
    connect_args = {"check_same_thread": False} if database_url.startswith("sqlite") else {}
    engine = create_engine(database_url, connect_args=connect_args)
    Base.metadata.create_all(engine)
    columns = {column["name"] for column in inspect(engine).get_columns("generations")}
    if "current_version_id" not in columns:
        with engine.begin() as connection:
            connection.execute(text("ALTER TABLE generations ADD COLUMN current_version_id INTEGER"))
    if database_url.startswith("sqlite"):
        version_columns = {column["name"] for column in inspect(engine).get_columns("versions")}
        if "is_matched" in version_columns:
            with engine.begin() as connection:
                connection.execute(text("ALTER TABLE versions DROP COLUMN is_matched"))
        plan_columns = {column["name"] for column in inspect(engine).get_columns("post_plan")}
        if "sheet_name" not in plan_columns:
            with engine.begin() as connection:
                connection.execute(text("ALTER TABLE post_plan ADD COLUMN sheet_name VARCHAR(200) NOT NULL DEFAULT 'Сентябрь'"))
        # Настройки до версии единого JSON-объекта хранились отдельными строками.
        # Их удаление безопасно: при следующем чтении будут использованы стандарты.
        with engine.begin() as connection:
            connection.execute(text("DELETE FROM prompt_settings WHERE name != 'prompt_settings'"))
    return sessionmaker(bind=engine, autoflush=False, autocommit=False)
