-- TRT's拡張
-- VoiceroidUtil_TextSplitに対応する処理を実行する

local P = {}

P.name = "TEXTSPLIT_EXO_HANDLER"

P.priority = 1

local avoiddupP = require("avoiddup")

local function getextension(filepath)
    return filepath:match(".[^.]+$"):lower()
end

local function trimextension(filepath)
    local ext = getextension(filepath)
    return filepath:sub(1, #filepath - #ext)
end

local function fileexists(filepath)
    local f = io.open(filepath, "rb")
    if f ~= nil then
        f:close()
        return true
    end
    return false
end

local function fileread(filepath)
    local f = io.open(filepath, "rb")
    if f == nil then
        return nil
    end
    local b = f:read("*all")
    f:close()
    return b
end

local function postprocesssubtitle(subtitle, encoding, setting)
    -- BOM がある場合はそれを基準にエンコーディング設定を上書きし、
    -- ついでに BOM もカットする
    if subtitle:sub(1, 3) == "\239\187\191" then
      encoding = "utf8"
      subtitle = subtitle:sub(4)
    elseif subtitle:sub(1, 2) == "\255\254" then
      encoding = "utf16le"
      subtitle = subtitle:sub(3)
    elseif subtitle:sub(1, 2) == "\254\255" then
      encoding = "utf16be"
      subtitle = subtitle:sub(3)
    end
    if encoding ~= "utf8" then
      -- 内部の保持状態を UTF-8 に統一する
      subtitle = GCMZDrops.convertencoding(subtitle, encoding, "utf8")
    end

    -- 置換用処理を呼び出す
    subtitle = setting:wav_subtitle_replacer(subtitle)

    return subtitle
end

local function requireLocal(pkg)
    local origpath = package.path
    local origcpath = package.cpath
    package.path = GCMZDrops.scriptdir() .. "..\\script\\PSDToolKit\\?.lua"
    package.cpath = GCMZDrops.scriptdir() .. "..\\script\\PSDToolKit\\?.dll"
    local ok, r = pcall(require, pkg)
    package.path = origpath
    package.cpath = origcpath
    if not ok then
      error(r)
    end
    return r
end

local function loadjsonstr(jsonstr)
    if jsonstr:sub(1, 3) == "\239\187\191" then
        jsonstr = jsonstr:sub(4)
    end
    local j = requireLocal("json").decode(jsonstr)
    if j == nil then
        return nil
    end
    return j
end

local function loadjson(filepath)
    local jsonstr = fileread(filepath)
    if jsonstr == nil then
        return nil
    end
    return loadjsonstr(jsonstr)
end

function P.resolvepath(filepath, finder, setting)
    if finder == 1 then
        return filepath:match("([^\\]+)[\\][^\\]+$")
    elseif finder == 2 then
        return filepath:match("([^\\]+)%.[^.]+$")
    elseif finder == 3 then
        return filepath:match("([^\\]+)%.[^.]+$"):match("^[^_]+")
    elseif finder == 4 then
        return filepath:match("([^\\]+)%.[^.]+$"):match("^[^_]+_([^_]+)")
    elseif finder == 5 then
        return filepath:match("([^\\]+)%.[^.]+$"):match("^[^_]+_[^_]+_([^_]+)")
    elseif type(finder) == "function" then
        return finder(setting, filepath)
    end
    return nil
end

function P.loadsetting()
    if P.setting ~= nil then
        return P.setting
    end
    local origpath = package.path
    package.path = GCMZDrops.scriptdir() .. "..\\script\\PSDToolKit\\?.lua"
    local ok, gui = pcall(require, "setting-gui")
    if not ok then gui = {} end
    local ok, user = pcall(require, "setting")
    if not ok then user = {} end
    P.setting = setmetatable(user, {__index = setmetatable(gui, {__index = require("default")})})
    package.path = origpath
    return P.setting
end

function P.ondragenter(files, state)
    for i, v in ipairs(files) do 
        local ext = getextension(v.filepath)
        if ext == ".exo" then
            -- ファイルの拡張子が .exo のファイルがあったら処理できるかもしれないので true
            return true
        end
    end
    return false
end

function P.ondragover(files, state)
    -- ondragenter で処理できそうなものは ondragover でも処理できそうなので調べず true
    return true
end

function P.ondragleave()
end

function P.exaread(filepath, postfix)
    local basepath = GCMZDrops.scriptdir() .. "..\\script\\PSDToolKit\\exa\\"
    local inistr = nil
    if filepath ~= nil then
        filepath = basepath .. filepath .. "_" .. postfix .. ".exa"
        inistr = fileread(filepath)
        if inistr == nil then
            debug_print("読み込み失敗: " .. filepath)
        end
    end
    if inistr == nil then
        filepath = basepath .. postfix .. ".exa"
        inistr = fileread(filepath)
    end
    if inistr ~= nil then
        debug_print("使用するエイリアスファイル: " .. filepath)
    else
        error("cannot read: " .. filepath)
    end
    return GCMZDrops.inistring(inistr)
end

function P.findexatype(ini)
    if ini:sectionexists("vo") then
        return "vo"
    elseif ini:sectionexists("ao") then
        return "ao"
    end
    error("unexpected alias file format")
end

function P.numitemsections(ini)
    local prefix = P.findexatype(ini)
    local n = 0
    while ini:sectionexists(prefix .. "." .. n) do
        n = n + 1
    end
    return n
end

function P.insertexa(destini, srcini, index, layer)
    local prefix = P.findexatype(srcini)
    destini:set(index, "layer", layer)
    destini:set(index, "overlay", 1)
    for _, key in ipairs(srcini:keys(prefix)) do
        if key ~= "length" then
            destini:set(index, key, srcini:get(prefix, key, ""))
        end
    end
    if prefix == "ao" then
        destini:set(index, "audio", 1)
    end
    for i = 0, P.numitemsections(srcini) - 1 do
        local exosection = index .. "." .. i
        local section = prefix .. "." .. i
        for _, key in ipairs(srcini:keys(section)) do

            destini:set(exosection, key, srcini:get(section, key, ""))
        end
    end
end

function P.parseexo(filepath)
    local exo = fileread(filepath)
    if exo == nil then
        return nil
    end

    local ini = GCMZDrops.inistring(exo)
    local wav, txtlist, j = nil, {}, nil
    local txtindex = 1
    local i = 0
    while 1 do
        if i == 260 then
            -- 260:VoiceroidUtilで1文字ずつ分割した場合の最大分割数
            return nil
        end
        local name = ini:get(i .. ".0", "_name", "")
        if name == "" then
            if i == 2 then
                return nil
            end
            break
        end

        if wav == nil and ((name == "音声ファイル")or(name == "Audio file")) then
            wav = ini:get(i .. ".0", "file", nil)
            if j == nil or j == '' then
                j = ini:get(i .. ".0", "__json", nil)
            end
        elseif (name == "テキスト")or(name == "Text") then
            txtlist[txtindex] = ini:get(i .. ".0", "text", nil)
            if j == nil or j == '' then
                j = ini:get(i .. ".0", "__json", nil)
            end
            txtindex = txtindex + 1
        end
        i = i + 1
    end

    if wav == nil or #txtlist == 0 then
        return nil
    end

    for i=1,#txtlist do
        txtlist[i] = GCMZDrops.decodeexotextutf8(txtlist[i])
    end

    if j ~= nil and j ~= '' then
        j = loadjsonstr(GCMZDrops.decodeexotextutf8(j))
    end
    return wav, txtlist, j
end

function P.fire(files, state)
    local setting = P.loadsetting()
    -- setting.wav_firemode_exo に適合するかチェック
    if setting.wav_firemode_exo == 1 then 
        for i, v in ipairs(files) do
            if getextension(v.filepath) == ".exo" then
                local orgwav, txtlist, j = P.parseexo(v.filepath)
                if orgwav ~= nil and fileexists(orgwav) then
                    local newwav = orgwav
                    if newwav ~= nil and GCMZDrops.needcopy(newwav) then
                        newwav = avoiddupP.getfile(newwav)
                        if newwav == '' then
                            newwav = nil
                        end
                    end
                    if newwav ~= nil and #txtlist ~= 0 then
                        local subtitlelist = {}
                        for i=1,#txtlist do
                            subtitlelist[i] = postprocesssubtitle(txtlist[i], "utf8", setting)
                        end
                        local exabase = P.resolvepath(orgwav, setting.wav_exafinder, setting)
                        if j == nil then
                            j = loadjson(trimextension(orgwav) .. ".json")
                        end
                        return newwav, subtitlelist, exabase, j
                    end
                end
            end
        end
    end
    return nil
end

function P.ondrop(files, state)
    local wavfilepath, subtitlelist, exabase, j = P.fire(files, state)
    if wavfilepath ~= nil and subtitlelist ~= nil then
        -- プロジェクトとファイルの情報を取得する
        local proj = GCMZDrops.getexeditfileinfo()
        local fi = GCMZDrops.getfileinfo(wavfilepath)
        -- 音声が現在のプロジェクトで何フレーム分あるのかを計算する
        local wavlen = math.ceil((fi.audio_samples * proj.rate) / (proj.audio_rate * proj.scale))

        return P.generateexo(wavfilepath, wavlen, subtitlelist, exabase, state, j)
    end
    return false
end

function P.generateexo(wavfilepath, wavlen, subtitlelist, exabase, state, j)
    local setting = P.loadsetting()
    -- テンプレート用変数を準備
    local values = {
        WAV_START = 1,
        WAV_END = 0,
        WAV_PATH = wavfilepath,
        LIPSYNC_START = 1,
        LIPSYNC_END = 0,
        LIPSYNC_PATH = wavfilepath,
        MPSLIDER_START = 1,
        MPSLIDER_END = 0,
        SUBTITLE_START_LIST = {},
        SUBTITLE_END_LIST = {},
        SUBTITLE_TEXT_LIST = subtitlelist,
        USER = j or {},
    }
    local modifiers = {
        ENCODE_TEXT = function(v)
            return GCMZDrops.encodeexotextutf8(v)
        end,
        ENCODE_LUA_STRING = function(v)
            v = GCMZDrops.convertencoding(v, "sjis", "utf8")
            v = GCMZDrops.encodeluastring(v)
            v = GCMZDrops.convertencoding(v, "utf8", "sjis")
            return v
        end,
    }

    -- 長さを反映
    values.WAV_END = values.WAV_END + wavlen
    values.LIPSYNC_END = values.LIPSYNC_END + wavlen
    values.MPSLIDER_END = values.MPSLIDER_END + wavlen
    for i=1,#subtitlelist do
        values.SUBTITLE_END_LIST[i] = wavlen * i / #subtitlelist
    end

    -- オフセットとマージンを反映
    values.LIPSYNC_START = values.LIPSYNC_START + setting.wav_lipsync_offset
    values.LIPSYNC_END = values.LIPSYNC_END + setting.wav_lipsync_offset
    values.MPSLIDER_START = values.MPSLIDER_START - setting.wav_mpslider_margin_left
    values.MPSLIDER_END = values.MPSLIDER_END + setting.wav_mpslider_margin_right
    values.SUBTITLE_START_LIST[1] = 1 - setting.wav_subtitle_margin_left
    if #subtitlelist > 1 then 
        for i=2,#subtitlelist do
            values.SUBTITLE_START_LIST[i] = values.SUBTITLE_END_LIST[i-1] + 1
        end
        values.SUBTITLE_END_LIST[#subtitlelist-1]
        = values.SUBTITLE_END_LIST[#subtitlelist-1] + setting.wav_subtitle_margin_right
    end

    -- マイナス方向に進んでしまった分を戻す
    local ofs = math.min(values.LIPSYNC_START, values.MPSLIDER_START, values.SUBTITLE_START_LIST[1]) - 1
    values.WAV_START = values.WAV_START - ofs
    values.WAV_END = values.WAV_END - ofs
    values.LIPSYNC_START = values.LIPSYNC_START - ofs
    values.LIPSYNC_END = values.LIPSYNC_END - ofs
    values.MPSLIDER_START = values.MPSLIDER_START - ofs
    values.MPSLIDER_END = values.MPSLIDER_END - ofs
    for i=1,#subtitlelist do
        values.SUBTITLE_START_LIST[i] = values.SUBTITLE_START_LIST[i] - ofs
        values.SUBTITLE_END_LIST[i] = values.SUBTITLE_END_LIST[i] - ofs
    end

    -- exo ファイルのヘッダ部分を組み立て
    local proj = GCMZDrops.getexeditfileinfo()
    local oini = GCMZDrops.inistring("")
    local totallen = math.max(values.WAV_END, values.LIPSYNC_END, values.MPSLIDER_END, values.SUBTITLE_END_LIST[#subtitlelist])
    oini:set("exedit", "width", proj.width)
    oini:set("exedit", "height", proj.height)
    oini:set("exedit", "rate", proj.rate)
    oini:set("exedit", "scale", proj.scale)
    oini:set("exedit", "length", totallen)
    oini:set("exedit", "audio_rate", proj.audio_rate)
    oini:set("exedit", "audio_ch", proj.audio_ch)

    -- オブジェクトの挿入
    local index = 0

    -- 音声を組み立て
    if wavfilepath ~= nil then
        local aini = P.exaread(exabase, "wav")
        setting:wav_examodifler_wav(aini, values, modifiers)
        P.insertexa(oini, aini, index, index + 1)
        index = index + 1
    end

    if setting.wav_mergedprep > 0 then
        -- 準備オブジェクトを組み立て
        local aini = GCMZDrops.inistring("")
        setting:wav_examodifler_mergedprep(aini, values, modifiers)
        P.insertexa(oini, aini, index, index + 1)
        index = index + 1
    else
        -- 口パク準備を組み立て
        if wavfilepath ~= nil and setting.wav_lipsync == 1 then
            local aini = P.exaread(exabase, "lipsync")
            setting:wav_examodifler_lipsync(aini, values, modifiers)
            P.insertexa(oini, aini, index, index + 1)
            index = index + 1
        end

        -- 多目的スライダーを組み立て
        if setting.wav_mpslider > 0 then
            local aini = GCMZDrops.inistring("")
            setting:wav_examodifler_mpslider(aini, values, modifiers)
            P.insertexa(oini, aini, index, index + 1)
            index = index + 1
        end

        -- 字幕準備を組み立て
        for i=1,#subtitlelist do
            if setting.wav_subtitle > 0 and values.SUBTITLE_TEXT_LIST[i] ~= "" then
                local aini = P.exaread(exabase, "subtitle")
                setting:wav_examodifler_subtitle_list(aini, values, modifiers, i)
                P.insertexa(oini, aini, index, index+(2-i))
                index = index + 1
            end
        end
    end

    local filepath = GCMZDrops.createtempfile("wav", ".exo")
    f, err = io.open(filepath, "wb")
    if f == nil then
        error(err)
    end

    f:write(tostring(oini))
    f:close()
    debug_print("["..P.name.."] がドロップされたファイルを exo ファイルに差し替えました。")

    if state.frameadvance ~= nil and state.frameadvance > 0 then
        state.frameadvance = totallen
        if values.USER and values.USER.padding then
            state.frameadvance = state.frameadvance + values.USER.padding
        end
    end
    return {{filepath=filepath}}, state
end

return P
