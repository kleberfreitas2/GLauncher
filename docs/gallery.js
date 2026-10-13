const carousel = document.querySelector("[data-gallery-carousel]");
const lightbox = document.querySelector("[data-lightbox]");

if (carousel && lightbox) {
  const slides = [...carousel.querySelectorAll("[data-gallery-slide]")];
  const dotsContainer = carousel.querySelector("[data-gallery-dots]");
  const caption = carousel.querySelector("[data-gallery-caption]");
  const count = carousel.querySelector("[data-gallery-count]");
  const autoplayButton = carousel.querySelector("[data-gallery-toggle]");
  const lightboxImage = lightbox.querySelector("[data-lightbox-image]");
  const lightboxCaption = lightbox.querySelector("[data-lightbox-caption]");
  let activeIndex = 0;
  let autoplayTimer = null;
  const prefersReducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  let userPausedAutoplay = prefersReducedMotion;

  function stopAutoplay() {
    window.clearInterval(autoplayTimer);
    autoplayTimer = null;
  }

  function startAutoplay(force = false) {
    stopAutoplay();
    const focusedControl = carousel.contains(document.activeElement) && document.activeElement !== autoplayButton;
    if (userPausedAutoplay || document.hidden || lightbox.open ||
        (!force && (carousel.matches(":hover") || focusedControl)))
      return;

    autoplayTimer = window.setInterval(() => showSlide(activeIndex + 1, false), 6_500);
  }

  function syncAutoplayButton() {
    autoplayButton.textContent = userPausedAutoplay ? "Reproduzir" : "Pausar";
    autoplayButton.setAttribute("aria-pressed", String(userPausedAutoplay));
    autoplayButton.setAttribute("aria-label", userPausedAutoplay ? "Retomar troca automática" : "Pausar troca automática");
  }

  const dots = slides.map((slide, index) => {
    const dot = document.createElement("button");
    dot.type = "button";
    dot.className = "gallery-dot";
    dot.setAttribute("aria-label", `Mostrar imagem ${index + 1}`);
    dot.addEventListener("click", () => showSlide(index));
    dotsContainer.append(dot);
    return dot;
  });

  function showSlide(index, restartAutoplay = true) {
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
    if (restartAutoplay) startAutoplay();
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
      stopAutoplay();
    });
  });

  autoplayButton.addEventListener("click", () => {
    userPausedAutoplay = !userPausedAutoplay;
    syncAutoplayButton();
    if (userPausedAutoplay) stopAutoplay();
    else startAutoplay(true);
  });

  lightbox.querySelector("[data-lightbox-close]").addEventListener("click", () => lightbox.close());
  lightbox.addEventListener("close", () => startAutoplay(true));
  lightbox.addEventListener("click", (event) => {
    if (event.target === lightbox) lightbox.close();
  });

  carousel.addEventListener("keydown", (event) => {
    if (event.key === "ArrowLeft") showSlide(activeIndex - 1);
    if (event.key === "ArrowRight") showSlide(activeIndex + 1);
  });
  carousel.addEventListener("mouseenter", stopAutoplay);
  carousel.addEventListener("mouseleave", () => startAutoplay());
  carousel.addEventListener("focusin", stopAutoplay);
  carousel.addEventListener("focusout", (event) => {
    if (!carousel.contains(event.relatedTarget)) startAutoplay();
  });
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) stopAutoplay();
    else startAutoplay();
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
  syncAutoplayButton();
}
