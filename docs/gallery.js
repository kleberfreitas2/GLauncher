const carousel = document.querySelector("[data-gallery-carousel]");
const lightbox = document.querySelector("[data-lightbox]");

if (carousel && lightbox) {
  const slides = [...carousel.querySelectorAll("[data-gallery-slide]")];
  const dotsContainer = carousel.querySelector("[data-gallery-dots]");
  const caption = carousel.querySelector("[data-gallery-caption]");
  const count = carousel.querySelector("[data-gallery-count]");
  const lightboxImage = lightbox.querySelector("[data-lightbox-image]");
  const lightboxCaption = lightbox.querySelector("[data-lightbox-caption]");
  let activeIndex = 0;

  const dots = slides.map((slide, index) => {
    const dot = document.createElement("button");
    dot.type = "button";
    dot.className = "gallery-dot";
    dot.setAttribute("aria-label", `Mostrar imagem ${index + 1}`);
    dot.addEventListener("click", () => showSlide(index));
    dotsContainer.append(dot);
    return dot;
  });

  function showSlide(index) {
    activeIndex = (index + slides.length) % slides.length;
    slides.forEach((slide, slideIndex) => {
      const isActive = slideIndex === activeIndex;
      slide.classList.toggle("is-active", isActive);
      slide.tabIndex = isActive ? 0 : -1;
      slide.setAttribute("aria-hidden", String(!isActive));
      dots[slideIndex].setAttribute("aria-current", String(isActive));
    });

    caption.textContent = slides[activeIndex].dataset.caption;
    count.textContent = `${String(activeIndex + 1).padStart(2, "0")} / ${String(slides.length).padStart(2, "0")}`;
  }

  carousel.querySelector("[data-gallery-previous]").addEventListener("click", () => showSlide(activeIndex - 1));
  carousel.querySelector("[data-gallery-next]").addEventListener("click", () => showSlide(activeIndex + 1));

  slides.forEach((slide) => {
    slide.addEventListener("click", () => {
      const image = slide.querySelector("img");
      lightboxImage.src = image.src;
      lightboxImage.alt = image.alt;
      lightboxCaption.textContent = slide.dataset.caption;
      lightbox.showModal();
    });
  });

  lightbox.querySelector("[data-lightbox-close]").addEventListener("click", () => lightbox.close());
  lightbox.addEventListener("click", (event) => {
    if (event.target === lightbox) lightbox.close();
  });

  carousel.addEventListener("keydown", (event) => {
    if (event.key === "ArrowLeft") showSlide(activeIndex - 1);
    if (event.key === "ArrowRight") showSlide(activeIndex + 1);
  });

  let touchStartX = null;
  carousel.querySelector(".gallery-stage").addEventListener("touchstart", (event) => {
    touchStartX = event.changedTouches[0].clientX;
  }, { passive: true });
  carousel.querySelector(".gallery-stage").addEventListener("touchend", (event) => {
    if (touchStartX === null) return;
    const deltaX = event.changedTouches[0].clientX - touchStartX;
    if (Math.abs(deltaX) > 45) showSlide(activeIndex + (deltaX < 0 ? 1 : -1));
    touchStartX = null;
  }, { passive: true });

  showSlide(0);
}
