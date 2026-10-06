"""
Google Flow Desktop - Task Engine & Batch Generation Algorithms
Implements Multi-Seed Generator, Cartesian Product Matrix, and Batch Reference Mapper.
"""
import uuid
import secrets
import itertools
from dataclasses import dataclass, field, asdict
from typing import List, Dict, Any, Optional

def secure_random_uint32() -> int:
    """Generates cryptographically secure 32-bit unsigned integer seed."""
    return secrets.randbits(32)

@dataclass
class TaskItem:
    task_id: str = field(default_factory=lambda: "task_" + uuid.uuid4().hex[:8])
    batch_id: str = ""
    prompt: str = ""
    negative_prompt: str = ""
    aspect_ratio: str = "1:1"
    seed: int = field(default_factory=secure_random_uint32)
    model: str = "Flow High-Quality (dGPU)"
    media_type: str = "image"  # 'image' or 'video'
    reference_path: Optional[str] = None
    task_type: str = "standard"  # 'standard', 'multi_seed', 'cartesian', 'reference'
    status: str = "waiting"  # 'waiting', 'generating', 'completed', 'failed'
    progress: int = 0
    output_path: Optional[str] = None
    sidecar_path: Optional[str] = None
    error_message: Optional[str] = None
    retry_count: int = 0
    created_at: float = field(default_factory=lambda: __import__('time').time())

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)

class TaskBatchEngine:
    @staticmethod
    def generate_multi_seed_batch(base_prompt: str,
                                   count: int = 4,
                                   model: str = "Flow High-Quality",
                                   negative_prompt: str = "",
                                   aspect_ratio: str = "1:1",
                                   media_type: str = "image") -> List[TaskItem]:
        """
        Генератор вариаций (Multi-Seed Generator):
        Цикл от 0 до N-1 генерирует объекты TaskItem, где каждому присваивается
        случайный seed = secure_random_uint32(), а текст и параметры идентичны.
        """
        batch_id = "batch_seed_" + uuid.uuid4().hex[:8]
        tasks = []
        for _ in range(max(1, count)):
            item = TaskItem(
                batch_id=batch_id,
                prompt=base_prompt,
                negative_prompt=negative_prompt,
                aspect_ratio=aspect_ratio,
                seed=secure_random_uint32(),
                model=model,
                media_type=media_type,
                task_type="multi_seed"
            )
            tasks.append(item)
        return tasks

    @staticmethod
    def generate_cartesian_matrix_batch(base_subject: str,
                                        style_tags: List[str],
                                        lighting_tags: List[str],
                                        optics_tags: Optional[List[str]] = None,
                                        model: str = "Flow High-Quality",
                                        negative_prompt: str = "",
                                        aspect_ratio: str = "16:9",
                                        media_type: str = "image") -> List[TaskItem]:
        """
        Матричный комбинатор (Cartesian Product Matrix):
        Декартово произведение массивов тегов склеивает независимые промпты:
        Subject + Style[i] + Lighting[j] + Optics[k].
        """
        batch_id = "batch_cartesian_" + uuid.uuid4().hex[:8]
        styles = style_tags if style_tags else [""]
        lights = lighting_tags if lighting_tags else [""]
        optics = optics_tags if optics_tags else [""]

        cartesian_product = list(itertools.product(styles, lights, optics))
        tasks = []

        for s, l, o in cartesian_product:
            parts = [base_subject.strip()]
            if s: parts.append(s.strip())
            if l: parts.append(l.strip())
            if o: parts.append(o.strip())
            full_prompt = ", ".join([p for p in parts if p])

            item = TaskItem(
                batch_id=batch_id,
                prompt=full_prompt,
                negative_prompt=negative_prompt,
                aspect_ratio=aspect_ratio,
                seed=secure_random_uint32(),
                model=model,
                media_type=media_type,
                task_type="cartesian"
            )
            tasks.append(item)
        return tasks

    @staticmethod
    def generate_batch_reference_mapper(image_paths: List[str],
                                        modifying_prompt: str,
                                        model: str = "Flow High-Quality",
                                        negative_prompt: str = "",
                                        aspect_ratio: str = "1:1",
                                        media_type: str = "image") -> List[TaskItem]:
        """
        Пакетная обработка изображений (Batch Reference Mapper):
        Создается ровно 1 задача на каждый входной файл, где файл передается
        с типом reference (не base, чтобы вариации не перезаписывали друг друга).
        """
        batch_id = "batch_ref_" + uuid.uuid4().hex[:8]
        tasks = []
        for path in image_paths:
            item = TaskItem(
                batch_id=batch_id,
                prompt=modifying_prompt,
                negative_prompt=negative_prompt,
                aspect_ratio=aspect_ratio,
                seed=secure_random_uint32(),
                model=model,
                media_type=media_type,
                reference_path=path,
                task_type="reference"
            )
            tasks.append(item)
        return tasks

if __name__ == "__main__":
    b1 = TaskBatchEngine.generate_multi_seed_batch("Cyberpunk hero in rain", count=3)
    print("Multi-seed items:", len(b1), [t.seed for t in b1])
    b2 = TaskBatchEngine.generate_cartesian_matrix_batch("Portrait of woman",
                                                        style_tags=["photorealistic", "oil painting"],
                                                        lighting_tags=["golden hour", "neon rim"])
    print("Cartesian items:", len(b2), [t.prompt for t in b2])
