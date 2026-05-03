// Shiny.Speech browser interop module for Web Speech API
let recognition = null;
let audioElement = null;
let recognitionStopped = false;

function getExports() {
    const { getAssemblyExports } = globalThis.getDotnetRuntime(0);
    return getAssemblyExports("Shiny.Speech");
}

export const shinySpeech = {
    // --- Speech Recognition (STT) ---
    isRecognitionSupported() {
        return !!(window.SpeechRecognition || window.webkitSpeechRecognition);
    },

    async requestMicrophoneAccess() {
        // 1. Request microphone permission
        try {
            const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
            stream.getTracks().forEach(t => t.stop());
        } catch (e) {
            console.warn('[Shiny.Speech] Microphone access denied:', e.message);
            return "denied";
        }

        // 2. Test that the speech recognition service is reachable
        const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
        if (!SpeechRecognition) return "not-supported";

        return new Promise((resolve) => {
            const testRecognition = new SpeechRecognition();
            testRecognition.continuous = false;
            testRecognition.interimResults = false;

            const timeout = setTimeout(() => {
                // If we got this far without error, the service is reachable
                testRecognition.abort();
                resolve("available");
            }, 2000);

            testRecognition.onerror = (event) => {
                clearTimeout(timeout);
                console.warn('[Shiny.Speech] Service test error:', event.error);
                if (event.error === 'not-allowed') {
                    resolve("denied");
                } else if (event.error === 'network') {
                    resolve("network");
                } else if (event.error === 'service-not-allowed') {
                    resolve("not-supported");
                } else if (event.error === 'no-speech' || event.error === 'aborted') {
                    // These are fine — means the service connected
                    resolve("available");
                } else {
                    resolve("error:" + event.error);
                }
            };

            testRecognition.onstart = () => {
                // Service connected successfully, no need to wait
                clearTimeout(timeout);
                testRecognition.abort();
                resolve("available");
            };

            try {
                testRecognition.start();
            } catch (e) {
                clearTimeout(timeout);
                resolve("error:" + e.message);
            }
        });
    },

    async startRecognition(lang, continuous) {
        const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
        if (!SpeechRecognition) {
            console.error('[Shiny.Speech] SpeechRecognition API not available');
            return;
        }

        recognitionStopped = false;
        let consecutiveErrors = 0;
        const maxRetries = 3;

        function createAndStart() {
            if (recognitionStopped) return;

            recognition = new SpeechRecognition();
            recognition.continuous = continuous;
            recognition.interimResults = true;
            if (lang) recognition.lang = lang;

            recognition.onresult = (event) => {
                consecutiveErrors = 0; // reset on successful result
                for (let i = event.resultIndex; i < event.results.length; i++) {
                    const result = event.results[i];
                    const text = result[0].transcript;
                    const isFinal = result.isFinal;
                    const confidence = result[0].confidence;

                    getExports().then(exports => {
                        exports.Shiny.Speech.BrowserSpeechToTextService.OnResult(text, isFinal, confidence);
                    });
                }
            };

            recognition.onend = () => {
                if (recognitionStopped) {
                    console.log('[Shiny.Speech] Recognition ended (stopped)');
                    getExports().then(exports => {
                        exports.Shiny.Speech.BrowserSpeechToTextService.OnEnd();
                    });
                } else if (continuous) {
                    console.log('[Shiny.Speech] Recognition ended, restarting (continuous mode)');
                    setTimeout(() => createAndStart(), 250);
                } else {
                    console.log('[Shiny.Speech] Recognition ended (single mode)');
                    getExports().then(exports => {
                        exports.Shiny.Speech.BrowserSpeechToTextService.OnEnd();
                    });
                }
            };

            recognition.onerror = (event) => {
                console.warn('[Shiny.Speech] Recognition error:', event.error);
                if (event.error === 'no-speech' || event.error === 'aborted') {
                    // Harmless: onend will fire and handle restart/end
                    return;
                }
                if (event.error === 'network') {
                    consecutiveErrors++;
                    if (consecutiveErrors <= maxRetries) {
                        console.warn(`[Shiny.Speech] Network error, retry ${consecutiveErrors}/${maxRetries}`);
                        return; // onend will restart
                    }
                    // Fall through to fatal after max retries
                    console.error('[Shiny.Speech] Network error persisted after retries');
                }
                // Fatal errors
                recognitionStopped = true;
                getExports().then(exports => {
                    exports.Shiny.Speech.BrowserSpeechToTextService.OnError(event.error);
                });
            };

            try {
                recognition.start();
                console.log('[Shiny.Speech] Recognition started', { lang, continuous });
            } catch (e) {
                console.error('[Shiny.Speech] Failed to start recognition:', e.message);
                recognitionStopped = true;
                getExports().then(exports => {
                    exports.Shiny.Speech.BrowserSpeechToTextService.OnError(e.message);
                });
            }
        }

        createAndStart();
    },

    stopRecognition() {
        console.log('[Shiny.Speech] stopRecognition called');
        recognitionStopped = true;
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
            getExports().then(exports => {
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
