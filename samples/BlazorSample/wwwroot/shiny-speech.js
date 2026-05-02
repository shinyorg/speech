// Shiny.Speech browser interop module for Web Speech API
globalThis.shinySpeech = (() => {
    let recognition = null;
    let audioElement = null;

    return {
        // --- Speech Recognition (STT) ---
        isRecognitionSupported() {
            return !!(window.SpeechRecognition || window.webkitSpeechRecognition);
        },

        startRecognition(lang, continuous) {
            const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
            if (!SpeechRecognition) return;

            recognition = new SpeechRecognition();
            recognition.continuous = continuous;
            recognition.interimResults = true;
            if (lang) recognition.lang = lang;

            recognition.onresult = (event) => {
                for (let i = event.resultIndex; i < event.results.length; i++) {
                    const result = event.results[i];
                    const text = result[0].transcript;
                    const isFinal = result.isFinal;
                    const confidence = result[0].confidence;

                    const { getAssemblyExports } = globalThis.getDotnetRuntime(0);
                    getAssemblyExports("Shiny.Speech").then(exports => {
                        exports.Shiny.Speech.BrowserSpeechToTextService.OnResult(text, isFinal, confidence);
                    });
                }
            };

            recognition.onend = () => {
                const { getAssemblyExports } = globalThis.getDotnetRuntime(0);
                getAssemblyExports("Shiny.Speech").then(exports => {
                    exports.Shiny.Speech.BrowserSpeechToTextService.OnEnd();
                });
            };

            recognition.onerror = (event) => {
                const { getAssemblyExports } = globalThis.getDotnetRuntime(0);
                getAssemblyExports("Shiny.Speech").then(exports => {
                    exports.Shiny.Speech.BrowserSpeechToTextService.OnError(event.error);
                });
            };

            recognition.start();
        },

        stopRecognition() {
            if (recognition) {
                recognition.stop();
                recognition = null;
            }
        },

        // --- Speech Synthesis (TTS) ---
        isSynthesisSupported() {
            return !!window.speechSynthesis;
        },

        getIsSpeaking() {
            return window.speechSynthesis?.speaking ?? false;
        },

        getVoices(cultureFilter) {
            if (!window.speechSynthesis) return "";

            const voices = window.speechSynthesis.getVoices();
            return voices
                .filter(v => !cultureFilter || v.lang.startsWith(cultureFilter))
                .map(v => `${v.voiceURI}|${v.name}|${v.lang}`)
                .join(";");
        },

        speak(text, lang, voiceUri, rate, pitch, volume) {
            if (!window.speechSynthesis) return;

            window.speechSynthesis.cancel();
            const utterance = new SpeechSynthesisUtterance(text);
            if (lang) utterance.lang = lang;
            utterance.rate = rate;
            utterance.pitch = pitch;
            utterance.volume = volume;

            if (voiceUri) {
                const voice = window.speechSynthesis.getVoices().find(v => v.voiceURI === voiceUri);
                if (voice) utterance.voice = voice;
            }

            utterance.onend = () => {
                const { getAssemblyExports } = globalThis.getDotnetRuntime(0);
                getAssemblyExports("Shiny.Speech").then(exports => {
                    exports.Shiny.Speech.BrowserTextToSpeechService.OnSpeakEnd();
                });
            };

            window.speechSynthesis.speak(utterance);
        },

        cancelSpeech() {
            window.speechSynthesis?.cancel();
        },

        // --- Audio Playback ---
        getIsPlaying() {
            return audioElement ? !audioElement.paused : false;
        },

        playAudio(dataUrl) {
            if (audioElement) {
                audioElement.pause();
                audioElement = null;
            }
            audioElement = new Audio(dataUrl);
            audioElement.play();
        },

        stopAudio() {
            if (audioElement) {
                audioElement.pause();
                audioElement.currentTime = 0;
                audioElement = null;
            }
        }
    };
})();

export { };
