// ブラウザの入力欄を出して、入れた文字を Unity に返す（Unity の入力欄は WebGL で日本語入力が使えないため）
mergeInto(LibraryManager.library, {
  KanjiPrompt: function (messagePtr) {
    var r = window.prompt(UTF8ToString(messagePtr), "");
    if (r === null) r = "";
    var size = lengthBytesUTF8(r) + 1;
    var buf = _malloc(size);
    stringToUTF8(r, buf, size);
    return buf;
  }
});
