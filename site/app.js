(() => {
  "use strict";

  const config = window.HONESTFLOW_SITE_CONFIG || {};
  const header = document.querySelector("[data-header]");
  const menuButton = document.querySelector("[data-menu-button]");
  const nav = document.querySelector("[data-nav]");
  const dialog = document.querySelector("[data-request-dialog]");
  const form = document.querySelector("[data-request-form]");
  const formStatus = document.querySelector("[data-form-status]");
  const submitButton = document.querySelector("[data-submit-request]");
  let returnFocus = null;
  let submitting = false;
  let requestPolicy = null;

  const syncHeader = () => header?.classList.toggle("is-scrolled", window.scrollY > 12);
  syncHeader();
  window.addEventListener("scroll", syncHeader, { passive: true });

  const closeMenu = () => {
    nav?.classList.remove("is-open");
    menuButton?.setAttribute("aria-expanded", "false");
  };

  menuButton?.addEventListener("click", () => {
    const isOpen = menuButton.getAttribute("aria-expanded") === "true";
    menuButton.setAttribute("aria-expanded", String(!isOpen));
    nav?.classList.toggle("is-open", !isOpen);
  });
  nav?.querySelectorAll("a").forEach((link) => link.addEventListener("click", closeMenu));
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && nav?.classList.contains("is-open")) {
      closeMenu();
      menuButton?.focus();
    }
  });

  const resetFormState = () => {
    if (!form || !submitButton || !formStatus) return;
    formStatus.className = "form-status";
    formStatus.textContent = requestPolicy?.available ? "" : "Приём заявок через сайт временно недоступен.";
    submitButton.disabled = submitting || !requestPolicy?.available;
    submitButton.textContent = submitting ? "Отправляем заявку…" : "Отправить заявку →";
    form.querySelectorAll("[aria-invalid]").forEach((field) => field.removeAttribute("aria-invalid"));
  };

  const openRequest = (trigger) => {
    if (!dialog) return;
    returnFocus = trigger instanceof HTMLElement ? trigger : document.activeElement;
    resetFormState();
    if (!requestPolicy) void loadRequestPolicy();
    if (dialog.open) return;
    if (typeof dialog.showModal === "function") dialog.showModal();
    else dialog.setAttribute("open", "");
  };

  const closeRequest = () => {
    if (!dialog) return;
    if (typeof dialog.close === "function") dialog.close();
    else {
      dialog.removeAttribute("open");
      if (returnFocus instanceof HTMLElement) returnFocus.focus();
    }
  };

  document.querySelectorAll("[data-open-request]").forEach((button) => {
    button.addEventListener("click", () => openRequest(button));
  });
  document.querySelector("[data-close-request]")?.addEventListener("click", closeRequest);
  dialog?.addEventListener("click", (event) => {
    if (event.target === dialog) closeRequest();
  });
  dialog?.addEventListener("close", () => {
    if (returnFocus instanceof HTMLElement && document.contains(returnFocus)) returnFocus.focus();
    returnFocus = null;
  });

  const getDownloadUrl = () => {
    try {
      const raw = String(config.downloadUrl || "").trim();
      if (!raw) return "";
      const url = new URL(raw, window.location.href);
      const isSameOriginPath = raw.startsWith("/") && url.origin === window.location.origin;
      return url.protocol === "https:" || isSameOriginPath ? url.href : "";
    } catch {
      return "";
    }
  };

  function initRelease() {
    if (!document.querySelector("[data-release-action]")) return;
    const setReleaseText = (selector, value) => {
      document.querySelectorAll(selector).forEach((element) => { element.textContent = value; });
    };
    const downloadUrl = getDownloadUrl();
    const releaseActions = document.querySelectorAll("[data-release-action]");
    const version = String(config.version || "не указана").trim();
    document.querySelectorAll("[data-release-version]").forEach((element) => {
      element.textContent = `Версия ${version}`;
    });

    if (downloadUrl) {
      releaseActions.forEach((button) => {
        button.innerHTML = "Скачать HonestFlow <span aria-hidden=\"true\">↓</span>";
        button.addEventListener("click", () => {
          const anchor = document.createElement("a");
          anchor.href = downloadUrl;
          anchor.rel = "noopener noreferrer";
          anchor.download = String(config.fileName || "HonestFlowSetup.exe");
          document.body.append(anchor);
          anchor.click();
          anchor.remove();
        });
      });
      setReleaseText("[data-release-status]", `Доступна версия ${version}`);
      setReleaseText("[data-release-source]", String(config.sourceLabel || new URL(downloadUrl).hostname));
      setReleaseText("[data-release-date]", String(config.publishedAt || "Дата не указана"));
    } else {
      releaseActions.forEach((button) => {
        button.textContent = "Уточнить получение приложения";
        button.addEventListener("click", () => {
          if (dialog) openRequest(button);
          else window.location.href = "../#connection";
        });
      });
      setReleaseText("[data-release-status]", `Версия ${version} готовится к публикации`);
    }
    const checksum = String(config.sha256 || "").trim();
    if (checksum) {
      const checksumRow = document.querySelector("[data-checksum-row]");
      const checksumValue = document.querySelector("[data-release-checksum]");
      if (checksumRow && checksumValue) {
        checksumValue.textContent = checksum;
        checksumRow.hidden = false;
      }
    }

  }

  function initDemo() {
    const demo = document.querySelector("[data-demo]");
    if (demo) {
      const start = demo.querySelector("[data-demo-start]");
      const reset = demo.querySelector("[data-demo-reset]");
      const progress = demo.querySelector("[data-demo-progress]");
      const meter = demo.querySelector("[data-demo-meter]");
      const symbol = demo.querySelector("[data-demo-symbol]");
      const title = demo.querySelector("[data-demo-title]");
      const text = demo.querySelector("[data-demo-text]");
      const heading = demo.querySelector("[data-demo-heading]");
      const pill = demo.querySelector("[data-demo-pill]");
      const node = demo.querySelector("[data-demo-node]");
      const nodeText = demo.querySelector("[data-demo-node-text]");
      const line = demo.querySelector("[data-demo-line]");
      const problem = demo.querySelector("[data-demo-problem]");
      if (![start, reset, progress, meter, symbol, title, text, heading, pill, node, nodeText, line, problem].every(Boolean)) return;
      const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
      let completionTimer = 0;

      const resetDemo = () => {
        window.clearTimeout(completionTimer);
        progress.hidden = true;
        reset.hidden = true;
        problem.hidden = false;
        start.disabled = false;
        meter.className = "";
        symbol.textContent = "↻";
        title.textContent = "Выполняется операция";
        text.textContent = "После запуска службы HonestFlow повторит диагностику.";
        heading.textContent = "Обнаружена проблема";
        pill.textContent = "Требуется действие";
        pill.classList.remove("is-ok");
        node.classList.add("node-warn");
        node.classList.remove("node-ok");
        nodeText.textContent = "Служба остановлена";
        line.classList.add("warn");
      };

      start.addEventListener("click", () => {
        start.disabled = true;
        progress.hidden = false;
        meter.classList.add("is-running");
        completionTimer = window.setTimeout(() => {
          problem.hidden = true;
          meter.classList.remove("is-running");
          meter.classList.add("is-complete");
          symbol.textContent = "✓";
          title.textContent = "Повторная проверка завершена";
          text.textContent = "Служба запущена. Связь с ККТ доступна.";
          heading.textContent = "Проблем не обнаружено";
          pill.textContent = "Проверка пройдена";
          pill.classList.add("is-ok");
          node.classList.remove("node-warn");
          node.classList.add("node-ok");
          nodeText.textContent = "Доступна";
          line.classList.remove("warn");
          reset.hidden = false;
        }, reducedMotion ? 20 : 1350);
      });
      reset.addEventListener("click", resetDemo);
    }
  }

  form?.querySelectorAll("input, textarea").forEach((field) => {
    field.addEventListener("invalid", () => field.setAttribute("aria-invalid", "true"));
    field.addEventListener("input", () => {
      if (field.name === "phone") field.setCustomValidity("");
      if (field.checkValidity()) field.removeAttribute("aria-invalid");
    });
  });

  form?.addEventListener("submit", async (event) => {
    event.preventDefault();
    if (submitting || !submitButton || !formStatus) return;
    if (!requestPolicy?.available) {
      formStatus.textContent = "Приём заявок через сайт временно недоступен.";
      return;
    }
    const phoneField = form.querySelector('[name="phone"]');
    const phone = phoneField.value.trim();
    const digitCount = (phone.match(/[0-9]/g) || []).length;
    const validPhone = /^\+?[0-9][0-9 ()-]*[0-9]$/.test(phone) &&
      digitCount >= 10 && digitCount <= 15 && !phone.startsWith("+0") &&
      (phone.startsWith("+") || (digitCount === 11 && /^[78]/.test(phone)));
    phoneField.setCustomValidity(validPhone ? "" : "Укажите российский номер или международный номер с кодом страны.");
    if (!form.checkValidity()) {
      formStatus.textContent = "Проверьте обязательные поля.";
      form.reportValidity();
      return;
    }

    const data = new FormData(form);
    const payload = {
      contactName: String(data.get("contactName") || "").trim(),
      company: optional(data.get("company")),
      phone,
      email: null,
      city: optional(data.get("city")),
      workplaceCount: Number(data.get("workplaceCount") || 1),
      inventorySystem: null,
      comment: optional([
        optional(data.get("cashRegisterModels")) ? `Модели ККТ: ${optional(data.get("cashRegisterModels"))}` : null,
        optional(data.get("comment"))
      ].filter(Boolean).join("\n\n")),
      source: "honestflow.ru",
      website: optional(data.get("website")),
      consent: data.get("consent") === "on",
      consentVersion: requestPolicy.consentVersion,
      privacyPolicyVersion: requestPolicy.privacyPolicyVersion
    };

    submitting = true;
    submitButton.disabled = true;
    submitButton.textContent = "Отправляем заявку…";
    formStatus.className = "form-status";
    formStatus.textContent = "";

    try {
      const response = await fetch("/api/connection-requests", {
        method: "POST",
        headers: { "Content-Type": "application/json", "Accept": "application/json" },
        body: JSON.stringify(payload)
      });

      if (response.status === 201 || response.status === 204) {
        form.reset();
        form.querySelector('[name="workplaceCount"]').value = "1";
        formStatus.classList.add("is-success");
        formStatus.textContent = "Заявка отправлена. Свяжемся с вами для согласования обслуживания в часы поддержки: пн–пт, 10:00–19:00 по Омску.";
        submitButton.textContent = "Заявка отправлена";
        return;
      }

      if (response.status === 409) {
        await loadRequestPolicy();
        throw new Error("Документы обновились. Ознакомьтесь с ними и отправьте заявку повторно.");
      }
      if (response.status === 429) throw new Error("Слишком много запросов. Попробуйте отправить заявку позже.");
      throw new Error(await readProblemMessage(response));
    } catch (error) {
      formStatus.textContent = error instanceof TypeError
        ? "Не удалось связаться с сервером. Проверьте соединение и повторите отправку."
        : error instanceof Error ? error.message : "Не удалось отправить заявку. Попробуйте позже.";
      submitButton.disabled = !requestPolicy?.available;
      submitButton.innerHTML = "Отправить заявку <span aria-hidden=\"true\">→</span>";
    } finally {
      submitting = false;
    }
  });

  async function loadRequestPolicy() {
    if (!form) return;
    try {
      const response = await fetch("/api/connection-requests/policy", { cache: "no-store", headers: { Accept: "application/json" } });
      if (!response.ok) throw new Error("Не удалось загрузить документы.");
      const policy = await response.json();
      const versionsMatch = policy.privacyPolicyVersion === form.dataset.privacyPolicyVersion &&
        policy.consentVersion === form.dataset.consentVersion && policy.consentRequired === true;
      requestPolicy = policy.available && versionsMatch ? policy : null;
      const consent = form.querySelector('[name="consent"]');
      consent.checked = false;
      consent.required = true;
      if (submitButton) submitButton.disabled = submitting || !requestPolicy;
      if (formStatus) formStatus.textContent = requestPolicy ? "" : policy.available
        ? "Редакции документов сайта и сервера различаются. Обновите страницу или повторите позже."
        : "Приём заявок через сайт временно недоступен.";
    } catch (error) {
      requestPolicy = null;
      if (submitButton) submitButton.disabled = true;
      if (formStatus) formStatus.textContent = "Не удалось загрузить документы. Закройте форму и попробуйте снова.";
      console.error("Не удалось загрузить документы формы", error);
    }
  }

  document.querySelectorAll("[data-provider-details]").forEach((element) => {
    element.textContent = (config.providerDetails || []).filter((value) => typeof value === "string").join(" · ");
  });
  void loadRequestPolicy();
  initRelease();
  initDemo();

  async function readProblemMessage(response) {
    const problem = await response.json().catch(() => null);
    if (problem?.message) return problem.message;
    if (problem?.title && response.status !== 500) return problem.title;
    if (problem?.errors) {
      const first = Object.values(problem.errors).flat().find(Boolean);
      if (first) return String(first);
    }
    return "Не удалось отправить заявку. Попробуйте позже.";
  }

  function optional(value) {
    const normalized = String(value || "").trim();
    return normalized || null;
  }
})();
