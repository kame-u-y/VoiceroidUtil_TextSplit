﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation;
using RucheHome.Util;

namespace RucheHome.Voiceroid
{
    partial class ProcessFactory
    {
        /// <summary>
        /// VOICEROID2用の IProcess インタフェース実装クラス。
        /// </summary>
        private sealed class Voiceroid2Impl : ImplBase
        {
            /// <summary>
            /// コンストラクタ。
            /// </summary>
            public Voiceroid2Impl() : base(VoiceroidId.Voiceroid2)
            {
            }

            /// <summary>
            /// ボタン種別列挙。
            /// </summary>
            private enum ButtonType
            {
                /// <summary>
                /// 再生
                /// </summary>
                Play,

                /// <summary>
                /// 停止
                /// </summary>
                Stop,

                /// <summary>
                /// 先頭
                /// </summary>
                Head,

                /// <summary>
                /// 末尾
                /// </summary>
                Tail,

                /// <summary>
                /// 音声保存
                /// </summary>
                Save,

                /// <summary>
                /// 再生時間
                /// </summary>
                Time,
            }

            /// <summary>
            /// ボタン上テキストとボタン種別のディクショナリ。
            /// </summary>
            private static readonly Dictionary<string, ButtonType> NamedButtonTypes =
                new Dictionary<string, ButtonType>
                {
                    { @"再生", ButtonType.Play },
                    { @"停止", ButtonType.Stop },
                    { @"先頭", ButtonType.Head },
                    { @"末尾", ButtonType.Tail },
                    { @"音声保存", ButtonType.Save },
                    { @"再生時間", ButtonType.Time },
                };

            /// <summary>
            /// 音声保存オプションウィンドウ名。
            /// </summary>
            /// <remarks>
            /// 本体側の設定次第で表示される。
            /// 表示される場合、以降の保存関連ダイアログはこのウィンドウの子となる。
            /// </remarks>
            private const string SaveOptionDialogName = @"音声保存";

            /// <summary>
            /// 音声保存ファイルダイアログ名。
            /// </summary>
            private const string SaveFileDialogName = @"名前を付けて保存";

            /// <summary>
            /// 音声保存進捗ウィンドウ名。
            /// </summary>
            private const string SaveProgressDialogName = @"音声保存";

            /// <summary>
            /// 音声保存完了ダイアログ名。
            /// </summary>
            private const string SaveCompleteDialogName = @"情報";

            /// <summary>
            /// ボタン群を検索する。
            /// </summary>
            /// <param name="types">検索対象ボタン種別配列。</param>
            /// <returns>ボタン AutomationElement 配列。見つからなければ null 。</returns>
            private async Task<AutomationElement[]> FindButtons(params ButtonType[] types)
            {
                if (types == null)
                {
                    throw new ArgumentNullException(nameof(types));
                }

                var root = AutomationElement.FromHandle(this.MainWindowHandle);
                var buttonsRoot = FindFirstChildByAutomationId(root, @"c");
                if (buttonsRoot == null)
                {
                    return null;
                }

                var results = new AutomationElement[types.Length];

                try
                {
                    await Task.Run(
                        () =>
                        {
                            // ボタン群取得
                            var buttons =
                                buttonsRoot.FindAll(
                                    TreeScope.Children,
                                    new PropertyCondition(
                                        AutomationElement.ControlTypeProperty,
                                        ControlType.Button));

                            foreach (AutomationElement button in buttons)
                            {
                                // 子のテキストからボタン種別決定
                                var buttonText =
                                    FindFirstChildByControlType(button, ControlType.Text);
                                if (
                                    NamedButtonTypes.TryGetValue(
                                        buttonText.Current.Name,
                                        out var type))
                                {
                                    var index = Array.IndexOf(types, type);
                                    if (index >= 0)
                                    {
                                        results[index] = button;
                                    }
                                }
                            }
                        });
                }
                catch
                {
                    return null;
                }

                // すべてのボタンが揃っているか確認
                if (results.Any(b => b == null))
                {
                    return null;
                }

                return results;
            }

            /// <summary>
            /// ダイアログ群を検索する。
            /// </summary>
            /// <returns>ダイアログ AutomationElement 配列。</returns>
            private async Task<AutomationElement[]> FindDialogs()
            {
                var mainHandle = this.MainWindowHandle;
                if (!this.IsRunning || mainHandle == IntPtr.Zero)
                {
                    return new AutomationElement[0];
                }

                var root = AutomationElement.FromHandle(mainHandle);

                try
                {
                    var dialogs =
                        await Task.Run(
                            () =>
                                FindChildWindows(root)
                                    .Where(e => e.Current.Name.Length > 0)
                                    .ToArray());

                    // ダイアログ表示中フラグを更新
                    this.IsDialogShowing = (dialogs.Length > 0);

                    return dialogs;
                }
                catch { }
                return new AutomationElement[0];
            }

            /// <summary>
            /// トークテクストエディットコントロールを検索する。
            /// </summary>
            /// <returns>見つかった AutomationElement 。見つからなければ null 。</returns>
            private AutomationElement FindTalkTextEdit()
            {
                var mainHandle = this.MainWindowHandle;
                if (!this.IsRunning || mainHandle == IntPtr.Zero)
                {
                    return null;
                }

                var root = AutomationElement.FromHandle(mainHandle);
                var editRoot = FindFirstChildByAutomationId(root, @"c");

                return FindFirstChildByControlType(editRoot, ControlType.Edit);
            }

            /// <summary>
            /// プリセット名テキストコントロールを検索する。
            /// </summary>
            /// <returns>見つかった AutomationElement 。見つからなければ null 。</returns>
            private AutomationElement FindPresetNameText()
            {
                var mainHandle = this.MainWindowHandle;
                if (!this.IsRunning || mainHandle == IntPtr.Zero)
                {
                    return null;
                }

                var root = AutomationElement.FromHandle(mainHandle);
                var textRoot = FindFirstChildByAutomationId(root, @"d");

                return FindFirstChildByControlType(textRoot, ControlType.Text);
            }

            #region 音声保存処理

            /// <summary>
            /// 音声保存ボタンを押下する処理を行う。
            /// </summary>
            /// <returns>成功したならば true 。そうでなければ false 。</returns>
            private async Task<bool> DoPushSaveButtonTask()
            {
                var save = (await this.FindButtons(ButtonType.Save))?[0];
                if (save == null)
                {
                    ThreadTrace.WriteLine(@"保存ボタンが見つかりません。");
                    return false;
                }

                try
                {
                    if (!(await Task.Run(() => InvokeElement(save))))
                    {
                        ThreadTrace.WriteLine(@"保存ボタンを押下できません。");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    ThreadTrace.WriteException(ex);
                    return false;
                }

                return true;
            }

            /// <summary>
            /// 音声保存ダイアログを検索する処理を行う。
            /// </summary>
            /// <returns>音声保存ダイアログ。見つからなければ null 。</returns>
            /// <remarks>
            /// オプションウィンドウかファイルダイアログのいずれかを返す。
            /// </remarks>
            private async Task<AutomationElement> DoFindSaveDialogTask()
            {
                try
                {
                    return
                        await RepeatUntil(
                            async () =>
                                (await this.FindDialogs())
                                    .FirstOrDefault(
                                        d =>
                                            d.Current.Name == SaveOptionDialogName ||
                                            d.Current.Name == SaveFileDialogName),
                            (AutomationElement d) => d != null,
                            150);
                }
                catch (Exception ex)
                {
                    ThreadTrace.WriteException(ex);
                }
                return null;
            }

            /// <summary>
            /// 音声保存オプションウィンドウのOKボタンを押下し、ファイルダイアログを取得する。
            /// </summary>
            /// <param name="dialog">音声保存オプションウィンドウ。</param>
            /// <returns>ファイルダイアログ。見つからなければ null 。</returns>
            private async Task<AutomationElement> DoPushOkButtonOfSaveOptionDialogTask(
                AutomationElement optionDialog)
            {
                if (optionDialog == null)
                {
                    throw new ArgumentNullException(nameof(optionDialog));
                }

                // 入力可能状態まで待機
                if (!(await this.WhenForInputHandle()))
                {
                    ThreadTrace.WriteLine(@"入力可能状態になりません。");
                    return null;
                }

                // OKボタン検索
                var okButton =
                    await RepeatUntil(
                        () =>
                            FindFirstChild(
                                optionDialog,
                                AutomationElement.NameProperty,
                                @"OK"),
                        elem => elem != null,
                        50);
                if (okButton == null)
                {
                    ThreadTrace.WriteLine(@"OKボタンが見つかりません。");
                    return null;
                }

                AutomationElement fileDialog = null;

                try
                {
                    // OKボタン押下
                    if (!InvokeElement(okButton))
                    {
                        ThreadTrace.WriteLine(@"OKボタンを押下できません。");
                        return null;
                    }

                    // ファイルダイアログ検索
                    fileDialog =
                        await RepeatUntil(
                            () =>
                                FindFirstChildByControlType(
                                    optionDialog,
                                    ControlType.Window),
                            elem => elem != null,
                            150);
                    if (fileDialog?.Current.Name != SaveFileDialogName)
                    {
                        ThreadTrace.WriteLine(@"ファイルダイアログが見つかりません。");
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    ThreadTrace.WriteException(ex);
                    return null;
                }

                return fileDialog;
            }

            /// <summary>
            /// WAVEファイルパスをファイル名エディットへ設定する処理を行う。
            /// </summary>
            /// <param name="fileNameEdit">ファイル名エディット。</param>
            /// <param name="filePath">WAVEファイルパス。</param>
            /// <returns>成功したならば true 。そうでなければ false 。</returns>
            private async Task<bool> DoSetFilePathToEditTask(
                AutomationElement fileNameEdit,
                string filePath)
            {
                if (fileNameEdit == null || string.IsNullOrWhiteSpace(filePath))
                {
                    return false;
                }

                // 入力可能状態まで待機
                if (!(await this.WhenForInputHandle()))
                {
                    ThreadTrace.WriteLine(@"入力可能状態になりません。");
                    return false;
                }

                // フォーカス
                try
                {
                    fileNameEdit.SetFocus();
                }
                catch (Exception ex)
                {
                    ThreadTrace.WriteException(ex);
                    return false;
                }

                // ファイルパス設定
                if (!SetElementValue(fileNameEdit, filePath))
                {
                    ThreadTrace.WriteLine(@"ファイルパスを設定できません。");
                    return false;
                }

                return true;
            }

            /// <summary>
            /// 既に存在するWAVEファイルの削除処理を行う。
            /// </summary>
            /// <param name="filePath">WAVEファイルパス。</param>
            /// <param name="withSplitFiles">
            /// 分割連番ファイルの削除も行うならば true 。
            /// </param>
            /// <returns>削除したファイル数。失敗したならば -1 。</returns>
            private async Task<int> DoEraseOldFileTask(
                string filePath,
                bool withSplitFiles = true)
            {
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    return -1;
                }

                int count = 0;

                // そのままの名前の wav, txt を削除
                var txtPath = Path.ChangeExtension(filePath, @".txt");
                try
                {
                    await Task.Run(
                        () =>
                        {
                            foreach (var path in new[] { filePath, txtPath })
                            {
                                if (File.Exists(path))
                                {
                                    File.Delete(path);
                                    ++count;
                                }
                            }
                        });
                }
                catch (Exception ex)
                {
                    ThreadTrace.WriteException(ex);
                    return -1;
                }

                // VOICEROID2のファイル分割機能による連番ファイルを削除
                if (withSplitFiles)
                {
                    for (int i = 0; ; ++i)
                    {
                        var splitPath =
                            Path.Combine(
                                Path.GetDirectoryName(filePath),
                                Path.GetFileNameWithoutExtension(filePath) + @"-" + i +
                                Path.GetExtension(filePath));
                        var c = await DoEraseOldFileTask(splitPath, false);
                        if (c <= 0)
                        {
                            break;
                        }
                        count += c;
                    }
                }

                return count;
            }

            /// <summary>
            /// ファイル名エディットコントロールの入力内容確定処理を行う。
            /// </summary>
            /// <param name="okButton">ファイルダイアログのOKボタン。</param>
            /// <param name="fileDialogParent">ファイルダイアログの親。</param>
            /// <returns>成功したならば true 。そうでなければ false 。</returns>
            private async Task<bool> DoDecideFilePathTask(
                AutomationElement okButton,
                AutomationElement fileDialogParent)
            {
                if (okButton == null || fileDialogParent == null)
                {
                    return false;
                }

                // 入力可能状態まで待機
                if (!(await this.WhenForInputHandle()))
                {
                    ThreadTrace.WriteLine(@"入力可能状態になりません。");
                    return false;
                }

                // フォーカス
                try
                {
                    okButton.SetFocus();
                }
                catch (Exception ex)
                {
                    ThreadTrace.WriteException(ex);
                    return false;
                }

                // OKボタン押下
                if (!InvokeElement(okButton))
                {
                    ThreadTrace.WriteLine(@"OKボタンを押下できません。");
                    return false;
                }

                // ファイルダイアログが閉じるまで待つ
                try
                {
                    var closed =
                        await RepeatUntil(
                            () =>
                                !FindChildWindows(
                                    fileDialogParent,
                                    SaveFileDialogName)
                                    .Any(),
                            f => f,
                            150);
                    if (!closed)
                    {
                        ThreadTrace.WriteLine(@"ファイルダイアログの終了を確認できません。");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    ThreadTrace.WriteException(ex);
                    return false;
                }

                return true;
            }

            /// <summary>
            /// WAVEファイルの保存確認処理を行う。
            /// </summary>
            /// <param name="filePath">WAVEファイルパス。</param>
            /// <param name="fileDialogParent">ファイルダイアログの親。</param>
            /// <returns>保存確認したファイルパス。確認できなければ null 。</returns>
            private async Task<string> DoCheckFileSavedTask(
                string filePath,
                AutomationElement fileDialogParent)
            {
                if (string.IsNullOrWhiteSpace(filePath) || fileDialogParent == null)
                {
                    return null;
                }

                // VOICEROID2機能でファイル分割される場合があるのでそちらも存在チェックする
                var splitFilePath =
                    Path.Combine(
                        Path.GetDirectoryName(filePath),
                        Path.GetFileNameWithoutExtension(filePath) + @"-0" +
                        Path.GetExtension(filePath));

                try
                {
                    // ファイル保存完了 or ダイアログ表示 を待つ
                    // ファイル保存完了なら null を返す
                    var dialogs =
                        await RepeatUntil(
                            () =>
                                (File.Exists(filePath) || File.Exists(splitFilePath)) ?
                                    null :
                                    FindChildWindows(fileDialogParent)
                                        .Where(d => d.Current.Name.Length > 0)
                                        .ToArray(),
                            dlgs => (dlgs == null || dlgs.Length > 0),
                            150);
                    if (dialogs != null)
                    {
                        var names = Array.ConvertAll(dialogs, d => d.Current.Name);

                        // 保存進捗、保存完了以外のダイアログが出ていたら失敗
                        if (
                            names.Any(
                                name =>
                                    name != SaveProgressDialogName &&
                                    name != SaveCompleteDialogName))
                        {
                            ThreadTrace.WriteLine(
                                @"保存関連以外のダイアログが開いています。 dialogs=" +
                                string.Join(@",", names));
                            return null;
                        }

                        // 保存進捗ダイアログが閉じるまで待つ
                        if (names.Any(name => name == SaveProgressDialogName))
                        {
                            await RepeatUntil(
                                () =>
                                    !FindChildWindows(
                                        fileDialogParent,
                                        SaveProgressDialogName)
                                        .Any(),
                                f => f);
                        }

                        // 改めてファイル保存完了チェック
                        bool saved =
                            await RepeatUntil(
                                () => File.Exists(filePath) || File.Exists(splitFilePath),
                                f => f,
                                25);
                        if (!saved)
                        {
                            return null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    ThreadTrace.WriteException(ex);
                    return null;
                }

                // 保存確認したファイルパス
                var resultPath = File.Exists(filePath) ? filePath : splitFilePath;

                // 同時にテキストファイルが保存される場合があるため少し待つ
                // 保存されていなくても失敗にはしない
                var txtPath = Path.ChangeExtension(resultPath, @".txt");
                await RepeatUntil(() => File.Exists(txtPath), f => f, 10);

                return resultPath;
            }

            /// <summary>
            /// 保存完了ダイアログを閉じる処理を行う。
            /// </summary>
            /// <remarks>
            /// 失敗しても構わない。
            /// </remarks>
            private async Task DoCloseFileSaveCompleteDialog()
            {
                // オプションウィンドウを表示する設定でもしない設定でも、
                //   メインウィンドウ -> "音声保存" ウィンドウ -> 保存完了ウィンドウ
                // というツリー構造になっている。

                try
                {
                    var root = AutomationElement.FromHandle(this.MainWindowHandle);

                    // "音声保存" ウィンドウを探す
                    var saveWindow =
                        await RepeatUntil(
                            () => FindChildWindows(root, @"音声保存").FirstOrDefault(),
                            d => d != null,
                            25);
                    if (saveWindow == null)
                    {
                        return;
                    }

                    // 保存完了ダイアログを探す
                    var dialog =
                        await RepeatUntil(
                            () =>
                                FindChildWindows(saveWindow, SaveCompleteDialogName)
                                    .FirstOrDefault(),
                            d => d != null,
                            50);
                    if (dialog == null)
                    {
                        return;
                    }

                    // OKボタンを探す
                    var okButton = FindFirstChildByControlType(dialog, ControlType.Button);
                    if (okButton == null)
                    {
                        return;
                    }

                    // OKボタンを押す
                    InvokeElement(okButton);
                }
                catch (Exception ex)
                {
                    ThreadTrace.WriteException(ex);
                }
            }

            #endregion

            #region ImplBase のオーバライド

            /// <summary>
            /// ボイスプリセット名を取得する。
            /// </summary>
            /// <returns>ボイスプリセット名。</returns>
            /// <remarks>
            /// 実行中の場合はボイスプリセット名を取得して返す。
            /// それ以外では Name の値をそのまま返す。
            /// </remarks>
            public override async Task<string> GetVoicePresetName()
            {
                string name = null;
                try
                {
                    name = this.FindPresetNameText()?.Current.Name;
                }
                catch
                {
                    name = null;
                }

                return name ?? (await base.GetVoicePresetName());
            }

            /// <summary>
            /// メインウィンドウタイトルであるか否かを取得する。
            /// </summary>
            /// <param name="title">タイトル。</param>
            /// <returns>
            /// メインウィンドウタイトルならば true 。そうでなければ false 。
            /// </returns>
            /// <remarks>
            /// スプラッシュウィンドウ等の判別用に用いる。
            /// </remarks>
            protected override bool IsMainWindowTitle(string title)
            {
                return (title?.Contains(@"VOICEROID2") == true);
            }

            /// <summary>
            /// メインウィンドウ変更時の更新処理を行う。
            /// </summary>
            /// <returns>更新できたならば true 。そうでなければ false 。</returns>
            protected override async Task<bool> UpdateOnMainWindowChanged()
            {
                // ボタンがあるか適当に調べておく
                return ((await this.FindButtons(ButtonType.Save)) != null);
            }

            /// <summary>
            /// IsDialogShowing プロパティ値を更新する。
            /// </summary>
            /// <returns>更新した値。</returns>
            protected override async Task<bool> UpdateDialogShowing()
            {
                // FindDialogs 内で IsDialogShowing が更新される
                await this.FindDialogs();

                return this.IsDialogShowing;
            }

            /// <summary>
            /// 現在WAVEファイル保存処理中であるか否か調べる。
            /// </summary>
            /// <returns>保存処理中ならば true 。そうでなければ false 。</returns>
            /// <remarks>
            /// 直接本体側を操作して保存処理を行っている場合にも true を返すこと。
            /// </remarks>
            protected override async Task<bool> CheckSaving()
            {
                // 下記のいずれかのダイアログが表示されているならば保存中
                // - "音声保存" (オプションウィンドウor保存進捗ウィンドウ)
                // - "名前を付けて保存" (ファイルダイアログ)
                try
                {
                    return
                        (await this.FindDialogs())
                            .Select(d => d.Current.Name)
                            .Any(
                                name =>
                                    name == SaveOptionDialogName ||
                                    name == SaveFileDialogName);
                }
                catch { }
                return false;
            }

            /// <summary>
            /// 現在再生中であるか否か調べる。
            /// </summary>
            /// <returns>再生中ならば true 。そうでなければ false 。</returns>
            /// <remarks>
            /// 直接本体側を操作して再生処理を行っている場合にも true を返すこと。
            /// </remarks>
            protected override async Task<bool> CheckPlaying()
            {
                // 保存ボタンが押せない状態＝再生中と判定
                try
                {
                    var save = (await this.FindButtons(ButtonType.Save))?[0];
                    return (save?.Current.IsEnabled == false);
                }
                catch { }
                return false;
            }

            /// <summary>
            /// トークテキスト取得の実処理を行う。
            /// </summary>
            /// <returns>トークテキスト。取得できなかった場合は null 。</returns>
            protected override async Task<string> DoGetTalkText()
            {
                var edit = this.FindTalkTextEdit();
                if (edit == null)
                {
                    return null;
                }

                try
                {
                    if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                    {
                        return await Task.Run(() => ((ValuePattern)pattern).Current.Value);
                    }
                }
                catch (Exception ex)
                {
                    ThreadTrace.WriteException(ex);
                }

                return null;
            }

            /// <summary>
            /// トークテキスト設定の実処理を行う。
            /// </summary>
            /// <param name="text">設定するトークテキスト。</param>
            /// <returns>成功したならば true 。そうでなければ false 。</returns>
            protected override async Task<bool> DoSetTalkText(string text)
            {
                var edit = this.FindTalkTextEdit();
                if (edit == null || !edit.Current.IsEnabled)
                {
                    return false;
                }

                return await Task.Run(() => SetElementValue(edit, text));
            }

            /// <summary>
            /// トークテキスト再生の実処理を行う。
            /// </summary>
            /// <returns>成功したならば true 。そうでなければ false 。</returns>
            protected override async Task<bool> DoPlay()
            {
                var buttons =
                    await this.FindButtons(
                        ButtonType.Head,
                        ButtonType.Play,
                        ButtonType.Save);
                if (buttons == null)
                {
                    return false;
                }
                var head = buttons[0];
                var play = buttons[1];
                var save = buttons[2];

                // 先頭ボタンと再生ボタン押下
                if (!(await Task.Run(() => InvokeElement(head) && InvokeElement(play))))
                {
                    return false;
                }

                try
                {
                    // 保存ボタンが無効になるかダイアログが出るまで待つ
                    // ダイアログが出ない限りは失敗にしない
                    await RepeatUntil(
                        async () =>
                            !save.Current.IsEnabled ||
                            (await this.UpdateDialogShowing()),
                        f => f,
                        25);
                }
                catch (Exception ex)
                {
                    ThreadTrace.WriteException(ex);
                    return false;
                }

                return !this.IsDialogShowing;
            }

            /// <summary>
            /// トークテキスト再生停止の実処理を行う。
            /// </summary>
            /// <returns>成功したならば true 。そうでなければ false 。</returns>
            protected override async Task<bool> DoStop()
            {
                var buttons = await this.FindButtons(ButtonType.Stop, ButtonType.Save);
                if (buttons == null)
                {
                    return false;
                }
                var stop = buttons[0];
                var save = buttons[1];

                // 停止ボタン押下
                if (!(await Task.Run(() => InvokeElement(stop))))
                {
                    return false;
                }

                // 保存ボタンが有効になるまで少し待つ
                var ok = false;
                try
                {
                    ok =
                        await RepeatUntil(
                            () => save.Current.IsEnabled,
                            f => f,
                            25);
                }
                catch (Exception ex)
                {
                    ThreadTrace.WriteException(ex);
                    return false;
                }

                return ok;
            }

            /// <summary>
            /// WAVEファイル保存処理を行える状態であるか否か調べる。
            /// </summary>
            /// <returns>行える状態ならば true 。そうでなければ false 。</returns>
            protected override async Task<bool> CanSave()
            {
                return ((await this.FindButtons(ButtonType.Save)) != null);
            }

            /// <summary>
            /// WAVEファイル保存の実処理を行う。
            /// </summary>
            /// <param name="filePath">保存希望WAVEファイルパス。</param>
            /// <returns>保存処理結果。</returns>
            protected override async Task<FileSaveResult> DoSave(string filePath)
            {
                // 保存ボタン押下
                if (!(await this.DoPushSaveButtonTask()))
                {
                    return new FileSaveResult(
                        false,
                        error: @"音声保存ボタンを押下できませんでした。");
                }

                // ダイアログ検索
                var rootDialog = await this.DoFindSaveDialogTask();
                if (rootDialog == null)
                {
                    var msg =
                        (await this.UpdateDialogShowing()) ?
                            @"音声保存を開始できませんでした。" :
                            @"音声保存ダイアログが見つかりませんでした。";
                    return new FileSaveResult(false, error: msg);
                }

                // オプションウィンドウを表示する設定か？
                bool optionShown = (rootDialog.Current.Name == SaveOptionDialogName);

                // オプションウインドウのOKボタンを押してファイルダイアログを出す
                // 最初からファイルダイアログが出ているならそのまま使う
                var fileDialog =
                    optionShown ?
                        (await this.DoPushOkButtonOfSaveOptionDialogTask(rootDialog)) :
                        rootDialog;
                if (fileDialog == null)
                {
                    return new FileSaveResult(
                        false,
                        error: @"ファイル保存ダイアログが見つかりませんでした。");
                }

                // ファイルダイアログの親
                // オプションウィンドウが表示されているならそれが親
                // 表示されていないならメインウィンドウが親
                var fileDialogParent =
                    optionShown ?
                        rootDialog : AutomationElement.FromHandle(this.MainWindowHandle);

                // OKボタンとファイル名エディットを検索
                var fileDialogElems = await this.DoFindFileDialogElements(fileDialog);
                if (fileDialogElems == null)
                {
                    return new FileSaveResult(
                        false,
                        error: @"ファイル名入力欄が見つかりませんでした。");
                }
                var okButton = fileDialogElems.Item1;
                var fileNameEdit = fileDialogElems.Item2;

                string extraMsg = null;

                // ファイル保存
                if (!(await this.DoSetFilePathToEditTask(fileNameEdit, filePath)))
                {
                    extraMsg = @"ファイル名を設定できませんでした。";
                }
                else if ((await this.DoEraseOldFileTask(filePath)) < 0)
                {
                    extraMsg = @"既存ファイルの削除に失敗しました。";
                }
                else if (!(await this.DoDecideFilePathTask(okButton, fileDialogParent)))
                {
                    extraMsg = @"ファイル名の確定操作に失敗しました。";
                }
                else
                {
                    filePath = await this.DoCheckFileSavedTask(filePath, fileDialogParent);
                    if (filePath == null)
                    {
                        extraMsg = @"ファイル保存を確認できませんでした。";
                    }
                    else
                    {
                        // 保存完了ダイアログを閉じる
                        await this.DoCloseFileSaveCompleteDialog();
                    }
                }

                // 追加情報が設定されていたら保存失敗
                if (extraMsg != null)
                {
                    return new FileSaveResult(
                        false,
                        error: @"ファイル保存処理に失敗しました。",
                        extraMessage: extraMsg);
                }

                return new FileSaveResult(true, filePath);
            }

            #endregion
        }
    }
}