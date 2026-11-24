// ===== SAFE READY =====
document.addEventListener("DOMContentLoaded", () => {

  const container = document.querySelector('.container');
  const loginForm = document.getElementById('loginForm');
  const registerForm = document.getElementById('registerForm');
  const registerBtn = document.querySelector('.register-btn');
  const loginBtn = document.querySelector('.login-btn');

  // Auto-fill username if available
  const lastUser = localStorage.getItem("last_logged_in_username");
  const usernameInput = document.getElementById("login_username");
  if (lastUser && usernameInput) {
    usernameInput.value = lastUser;
  }

  if (loginForm) loginForm.classList.add('active');
  if (container) container.classList.remove('active');

  function isMobile(){
    return window.innerWidth <= 768;
  }

  window.showRegisterForm = function(){
    if(window.innerWidth <= 768){
      if (registerForm) registerForm.classList.add('active');
      if (loginForm) loginForm.classList.remove('active');
    }else{
      if (container) container.classList.add('active');
    }
  };

  window.showLoginForm = function(){
    if(window.innerWidth <= 768){
      if (loginForm) loginForm.classList.add('active');
      if (registerForm) registerForm.classList.remove('active');
    }else{
      if (container) container.classList.remove('active');
    }
  };

  // ===== BUTTON PANEL DESKTOP =====
  if(registerBtn){
    registerBtn.onclick = window.showRegisterForm;
  }

  if(loginBtn){
    loginBtn.onclick = window.showLoginForm;
  }

  // ===== DEFAULT MOBILE =====
  function setupMobile(){
    if(isMobile() && container){
      container.classList.remove('active');
    }
  }

  setupMobile();
  window.addEventListener("resize", setupMobile);

});
