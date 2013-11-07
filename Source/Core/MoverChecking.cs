﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics.Contracts;
using System.Diagnostics;

namespace Microsoft.Boogie
{
    /*
     * Typechecking rules:
     * At most one atomic specification per procedure
     * The gate of an atomic specification refers only to global and input variables       
     */
    public class MoverChecking
    {
        HashSet<Tuple<ActionInfo, ActionInfo>> commutativityCheckerCache;
        HashSet<Tuple<ActionInfo, ActionInfo>> gatePreservationCheckerCache;
        HashSet<Tuple<ActionInfo, ActionInfo>> failurePreservationCheckerCache;
        LinearTypeChecker linearTypeChecker;
        Program moverCheckerProgram;
        private MoverChecking(LinearTypeChecker linearTypeChecker)
        {
            this.commutativityCheckerCache = new HashSet<Tuple<ActionInfo, ActionInfo>>();
            this.gatePreservationCheckerCache = new HashSet<Tuple<ActionInfo, ActionInfo>>();
            this.failurePreservationCheckerCache = new HashSet<Tuple<ActionInfo, ActionInfo>>();
            this.linearTypeChecker = linearTypeChecker;
            this.moverCheckerProgram = new Program();
            foreach (Declaration decl in linearTypeChecker.program.TopLevelDeclarations)
            {
                if (decl is TypeCtorDecl || decl is TypeSynonymDecl || decl is Constant || decl is Function || decl is Axiom)
                    this.moverCheckerProgram.TopLevelDeclarations.Add(decl);
            }
            foreach (Variable v in linearTypeChecker.program.GlobalVariables())
            {
                this.moverCheckerProgram.TopLevelDeclarations.Add(v);
            }
        }
        private sealed class MySubstituter : Duplicator
        {
            private readonly Substitution outsideOld;
            private readonly Substitution insideOld;
            [ContractInvariantMethod]
            void ObjectInvariant()
            {
                Contract.Invariant(insideOld != null);
            }

            public MySubstituter(Substitution outsideOld, Substitution insideOld)
                : base()
            {
                Contract.Requires(outsideOld != null && insideOld != null);
                this.outsideOld = outsideOld;
                this.insideOld = insideOld;
            }

            private bool insideOldExpr = false;

            public override Expr VisitIdentifierExpr(IdentifierExpr node)
            {
                Contract.Ensures(Contract.Result<Expr>() != null);
                Expr e = null;

                if (insideOldExpr)
                {
                    e = insideOld(node.Decl);
                }
                else
                {
                    e = outsideOld(node.Decl);
                }
                return e == null ? base.VisitIdentifierExpr(node) : e;
            }

            public override Expr VisitOldExpr(OldExpr node)
            {
                Contract.Ensures(Contract.Result<Expr>() != null);
                bool previouslyInOld = insideOldExpr;
                insideOldExpr = true;
                Expr tmp = (Expr)this.Visit(node.Expr);
                OldExpr e = new OldExpr(node.tok, tmp);
                insideOldExpr = previouslyInOld;
                return e;
            }
        }

        enum MoverType
        {
            Top,
            Atomic,
            Right,
            Left,
            Both
        }

        class ActionInfo
        {
            public Procedure proc;
            public MoverType moverType;
            public List<AssertCmd> thisGate;
            public CodeExpr thisAction;
            public List<Variable> thisInParams;
            public List<Variable> thisOutParams;
            public List<AssertCmd> thatGate;
            public CodeExpr thatAction;
            public List<Variable> thatInParams;
            public List<Variable> thatOutParams;

            public bool IsRightMover
            {
                get { return moverType == MoverType.Right || moverType == MoverType.Both; }
            }
            
            public bool IsLeftMover
            {
                get { return moverType == MoverType.Left || moverType == MoverType.Both; }
            }

            public ActionInfo(Procedure proc, CodeExpr codeExpr, MoverType moverType)
            {
                this.proc = proc;
                this.moverType = moverType;
                this.thisGate = new List<AssertCmd>();
                this.thisAction = codeExpr;
                this.thisInParams = new List<Variable>();
                this.thisOutParams = new List<Variable>();
                this.thatGate = new List<AssertCmd>();
                this.thatInParams = new List<Variable>();
                this.thatOutParams = new List<Variable>();

                var cmds = thisAction.Blocks[0].Cmds;
                for (int i = 0; i < cmds.Count; i++)
                {
                    AssertCmd assertCmd = cmds[i] as AssertCmd;
                    if (assertCmd == null) break;
                    thisGate.Add(assertCmd);
                    cmds[i] = new AssumeCmd(assertCmd.tok, assertCmd.Expr);
                }

                Dictionary<Variable, Expr> map = new Dictionary<Variable, Expr>();
                foreach (Variable x in proc.InParams)
                {
                    this.thisInParams.Add(x);
                    Variable y = new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "that_" + x.Name, x.TypedIdent.Type), true);
                    this.thatInParams.Add(y);
                    map[x] = new IdentifierExpr(Token.NoToken, y);
                }
                foreach (Variable x in proc.OutParams)
                {
                    this.thisOutParams.Add(x);
                    Variable y = new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "that_" + x.Name, x.TypedIdent.Type), false);
                    this.thatOutParams.Add(y);
                    map[x] = new IdentifierExpr(Token.NoToken, y);
                }
                List<Variable> otherLocVars = new List<Variable>();
                foreach (Variable x in thisAction.LocVars)
                {
                    Variable y = new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "that_" + x.Name, x.TypedIdent.Type), false);
                    map[x] = new IdentifierExpr(Token.NoToken, y);
                    otherLocVars.Add(y);
                }
                Contract.Assume(proc.TypeParameters.Count == 0);
                Substitution subst = Substituter.SubstitutionFromHashtable(map);
                foreach (AssertCmd assertCmd in thisGate)
                {
                    thatGate.Add((AssertCmd)Substituter.Apply(subst, assertCmd));
                }
                Dictionary<Block, Block> blockMap = new Dictionary<Block, Block>();
                List<Block> otherBlocks = new List<Block>();
                foreach (Block block in thisAction.Blocks)
                {
                    List<Cmd> otherCmds = new List<Cmd>();
                    foreach (Cmd cmd in block.Cmds)
                    {
                        otherCmds.Add(Substituter.Apply(subst, cmd));
                    }
                    Block otherBlock = new Block();
                    otherBlock.Cmds = otherCmds;
                    otherBlock.Label = "that_" + block.Label;
                    block.Label = "this_" + block.Label;
                    otherBlocks.Add(otherBlock);
                    blockMap[block] = otherBlock;
                    if (block.TransferCmd is GotoCmd)
                    {
                        GotoCmd gotoCmd = block.TransferCmd as GotoCmd;
                        for (int i = 0; i < gotoCmd.labelNames.Count; i++)
                        {
                            gotoCmd.labelNames[i] = "this_" + gotoCmd.labelNames[i];
                        }
                    }
                }
                foreach (Block block in thisAction.Blocks)
                {
                    if (block.TransferCmd is ReturnExprCmd)
                    {
                        blockMap[block].TransferCmd = new ReturnExprCmd(block.TransferCmd.tok, Expr.True);
                        continue;
                    }
                    List<Block> otherGotoCmdLabelTargets = new List<Block>();
                    List<string> otherGotoCmdLabelNames = new List<string>();
                    GotoCmd gotoCmd = block.TransferCmd as GotoCmd;
                    foreach (Block target in gotoCmd.labelTargets)
                    {
                        otherGotoCmdLabelTargets.Add(blockMap[target]);
                        otherGotoCmdLabelNames.Add(blockMap[target].Label);
                    }
                    blockMap[block].TransferCmd = new GotoCmd(block.TransferCmd.tok, otherGotoCmdLabelNames, otherGotoCmdLabelTargets);
                }
                this.thatAction = new CodeExpr(otherLocVars, otherBlocks);
            }
        }

        public static void AddCheckers(LinearTypeChecker linearTypeChecker)
        {
            Program program = linearTypeChecker.program;
            List<ActionInfo> gatedActions = new List<ActionInfo>();
            foreach (Declaration decl in program.TopLevelDeclarations)
            {
                Procedure proc = decl as Procedure;
                if (proc == null) continue;
                foreach (Ensures e in proc.Ensures)
                {
                    MoverType moverType = GetMoverType(e);
                    if (moverType == MoverType.Top) continue;
                    CodeExpr codeExpr = e.Condition as CodeExpr;
                    if (codeExpr == null)
                    {
                        Console.WriteLine("Warning: an atomic action must be a CodeExpr");
                        continue;
                    }
                    ActionInfo info = new ActionInfo(proc, codeExpr, moverType);
                    gatedActions.Add(info);
                }
            }
            if (gatedActions.Count == 0)
                return;
            MoverChecking moverChecking = new MoverChecking(linearTypeChecker);
            foreach (ActionInfo first in gatedActions)
            {
                Debug.Assert(first.moverType != MoverType.Top);
                if (first.moverType == MoverType.Atomic)
                    continue;
                foreach (ActionInfo second in gatedActions)
                {
                    if (first.IsRightMover)
                    {
                        moverChecking.CreateCommutativityChecker(program, first, second);
                        moverChecking.CreateGatePreservationChecker(program, second, first);
                    }
                    if (first.IsLeftMover)
                    {
                        moverChecking.CreateCommutativityChecker(program, second, first);
                        moverChecking.CreateGatePreservationChecker(program, first, second);
                        moverChecking.CreateFailurePreservationChecker(program, second, first);
                    }
                }
            }
            var eraser = new LinearEraser();
            eraser.VisitProgram(moverChecking.moverCheckerProgram);
            {
                int oldPrintUnstructured = CommandLineOptions.Clo.PrintUnstructured;
                CommandLineOptions.Clo.PrintUnstructured = 1;
                using (TokenTextWriter writer = new TokenTextWriter("MoverChecker.bpl", false))
                {
                    if (CommandLineOptions.Clo.ShowEnv != CommandLineOptions.ShowEnvironment.Never)
                    {
                        writer.WriteLine("// " + CommandLineOptions.Clo.Version);
                        writer.WriteLine("// " + CommandLineOptions.Clo.Environment);
                    }
                    writer.WriteLine();
                    moverChecking.moverCheckerProgram.Emit(writer);
                }
                CommandLineOptions.Clo.PrintUnstructured = oldPrintUnstructured;
            }
        }

        private static MoverType GetMoverType(Ensures e)
        {
            if (QKeyValue.FindBoolAttribute(e.Attributes, "atomic"))
                return MoverType.Atomic;
            else if (QKeyValue.FindBoolAttribute(e.Attributes, "right"))
                return MoverType.Right;
            else if (QKeyValue.FindBoolAttribute(e.Attributes, "left"))
                return MoverType.Left;
            else if (QKeyValue.FindBoolAttribute(e.Attributes, "both"))
                return MoverType.Both;
            else
                return MoverType.Top;
        }

        class TransitionRelationComputation
        {
            private Program program;
            private ActionInfo first;
            private ActionInfo second;
            private Stack<Block> dfsStack;
            private Expr transitionRelation;

            public TransitionRelationComputation(Program program, ActionInfo second)
            {
                this.program = program;
                this.first = null;
                this.second = second;
                this.dfsStack = new Stack<Block>();
                this.transitionRelation = Expr.False;
            }

            public TransitionRelationComputation(Program program, ActionInfo first, ActionInfo second)
            {
                this.program = program;
                this.first = first;
                this.second = second;
                this.dfsStack = new Stack<Block>();
                this.transitionRelation = Expr.False;
            }

            public Expr Compute()
            {
                Search(second.thatAction.Blocks[0], false);
                Dictionary<Variable, Expr> map = new Dictionary<Variable, Expr>();
                List<Variable> boundVars = new List<Variable>();
                if (first != null)
                {
                    foreach (Variable v in first.thisAction.LocVars)
                    {
                        BoundVariable bv = new BoundVariable(Token.NoToken, new TypedIdent(Token.NoToken, v.Name, v.TypedIdent.Type));
                        map[v] = new IdentifierExpr(Token.NoToken, bv);
                        boundVars.Add(bv);
                    }
                }
                foreach (Variable v in second.thatAction.LocVars)
                {
                    BoundVariable bv = new BoundVariable(Token.NoToken, new TypedIdent(Token.NoToken, v.Name, v.TypedIdent.Type));
                    map[v] = new IdentifierExpr(Token.NoToken, bv);
                    boundVars.Add(bv);
                }
                Substitution subst = Substituter.SubstitutionFromHashtable(map);
                if (boundVars.Count > 0)
                    return new ExistsExpr(Token.NoToken, boundVars, Substituter.Apply(subst, transitionRelation));
                else
                    return transitionRelation;
            }

            private Expr CalculatePathCondition()
            {
                Expr returnExpr = Expr.True;
                foreach (Variable v in program.GlobalVariables())
                {
                    var eqExpr = Expr.Eq(new IdentifierExpr(Token.NoToken, v), new OldExpr(Token.NoToken, new IdentifierExpr(Token.NoToken, v)));
                    returnExpr = Expr.And(eqExpr, returnExpr);
                }
                if (first != null)
                {
                    foreach (Variable v in first.thisOutParams)
                    {
                        var eqExpr = Expr.Eq(new IdentifierExpr(Token.NoToken, v), new OldExpr(Token.NoToken, new IdentifierExpr(Token.NoToken, v)));
                        returnExpr = Expr.And(eqExpr, returnExpr);
                    }
                }
                foreach (Variable v in second.thatOutParams)
                {
                    var eqExpr = Expr.Eq(new IdentifierExpr(Token.NoToken, v), new OldExpr(Token.NoToken, new IdentifierExpr(Token.NoToken, v)));
                    returnExpr = Expr.And(eqExpr, returnExpr);
                }
                Block[] dfsStackAsArray = dfsStack.Reverse().ToArray();
                for (int i = dfsStackAsArray.Length - 1; i >= 0; i--)
                {
                    Block b = dfsStackAsArray[i];
                    for (int j = b.Cmds.Count - 1; j >= 0; j--)
                    {
                        Cmd cmd = b.Cmds[j];
                        if (cmd is AssumeCmd)
                        {
                            AssumeCmd assumeCmd = cmd as AssumeCmd;
                            returnExpr = Expr.And(new OldExpr(Token.NoToken, assumeCmd.Expr), returnExpr);
                        }
                        else if (cmd is AssignCmd)
                        {
                            AssignCmd assignCmd = (cmd as AssignCmd).AsSimpleAssignCmd;
                            Dictionary<Variable, Expr> map = new Dictionary<Variable, Expr>();
                            for (int k = 0; k < assignCmd.Lhss.Count; k++)
                            {
                                map[assignCmd.Lhss[k].DeepAssignedVariable] = assignCmd.Rhss[k];
                            }
                            Substitution subst = Substituter.SubstitutionFromHashtable(new Dictionary<Variable, Expr>());
                            Substitution oldSubst = Substituter.SubstitutionFromHashtable(map);
                            returnExpr = (Expr) new MySubstituter(subst, oldSubst).Visit(returnExpr);
                        }
                        else
                        {
                            Debug.Assert(false);
                        }
                    }
                }
                return returnExpr;
            }

            private void Search(Block b, bool inFirst)
            {
                dfsStack.Push(b);
                if (b.TransferCmd is ReturnExprCmd)
                {
                    if (first == null || inFirst)
                    {
                        transitionRelation = Expr.Or(transitionRelation, CalculatePathCondition());
                    }
                    else
                    {
                        Search(first.thisAction.Blocks[0], true);
                    }
                }
                else
                {
                    GotoCmd gotoCmd = b.TransferCmd as GotoCmd;
                    foreach (Block target in gotoCmd.labelTargets)
                    {
                        Search(target, inFirst);
                    }
                }
                dfsStack.Pop();
            }
        }

        private static List<Block> CloneBlocks(List<Block> blocks)
        {
            Dictionary<Block, Block> blockMap = new Dictionary<Block, Block>();
            List<Block> otherBlocks = new List<Block>();
            foreach (Block block in blocks)
            {
                List<Cmd> otherCmds = new List<Cmd>();
                foreach (Cmd cmd in block.Cmds)
                {
                    otherCmds.Add(cmd);
                }
                Block otherBlock = new Block();
                otherBlock.Cmds = otherCmds;
                otherBlock.Label = block.Label;
                otherBlocks.Add(otherBlock);
                blockMap[block] = otherBlock;
            }
            foreach (Block block in blocks)
            {
                if (block.TransferCmd is ReturnExprCmd)
                {
                    blockMap[block].TransferCmd = new ReturnCmd(block.TransferCmd.tok);
                    continue;
                }
                List<Block> otherGotoCmdLabelTargets = new List<Block>();
                List<string> otherGotoCmdLabelNames = new List<string>();
                GotoCmd gotoCmd = block.TransferCmd as GotoCmd;
                foreach (Block target in gotoCmd.labelTargets)
                {
                    otherGotoCmdLabelTargets.Add(blockMap[target]);
                    otherGotoCmdLabelNames.Add(blockMap[target].Label);
                }
                blockMap[block].TransferCmd = new GotoCmd(block.TransferCmd.tok, otherGotoCmdLabelNames, otherGotoCmdLabelTargets);
            }
            return otherBlocks;
        }

        private List<Requires> DisjointnessRequires(Program program, ActionInfo first, ActionInfo second)
        {
            List<Requires> requires = new List<Requires>();
            Dictionary<string, HashSet<Variable>> domainNameToScope = new Dictionary<string, HashSet<Variable>>();
            foreach (var domainName in linearTypeChecker.linearDomains.Keys)
            {
                domainNameToScope[domainName] = new HashSet<Variable>();
            }
            foreach (Variable v in program.GlobalVariables())
            {
                var domainName = linearTypeChecker.FindDomainName(v);
                if (domainName == null) continue;
                domainNameToScope[domainName].Add(v);
            }
            foreach (Variable v in first.thisInParams)
            {
                var domainName = linearTypeChecker.FindDomainName(v);
                if (domainName == null) continue;
                domainNameToScope[domainName].Add(v);
            }
            for (int i = 0; i < second.thatInParams.Count; i++)
            {
                var domainName = linearTypeChecker.FindDomainName(second.thisInParams[i]);
                if (domainName == null) continue;
                domainNameToScope[domainName].Add(second.thatInParams[i]);
            }
            foreach (string domainName in domainNameToScope.Keys)
            {
                requires.Add(new Requires(false, linearTypeChecker.DisjointnessExpr(domainName, domainNameToScope[domainName])));
            }
            return requires;
        }

        private void CreateCommutativityChecker(Program program, ActionInfo first, ActionInfo second)
        {
            Tuple<ActionInfo, ActionInfo> actionPair = new Tuple<ActionInfo, ActionInfo>(first, second);
            if (commutativityCheckerCache.Contains(actionPair))
                return;
            commutativityCheckerCache.Add(actionPair);

            List<Variable> inputs = new List<Variable>();
            inputs.AddRange(first.thisInParams);
            inputs.AddRange(second.thatInParams);
            List<Variable> outputs = new List<Variable>();
            outputs.AddRange(first.thisOutParams);
            outputs.AddRange(second.thatOutParams);
            List<Variable> locals = new List<Variable>();
            locals.AddRange(first.thisAction.LocVars);
            locals.AddRange(second.thatAction.LocVars);
            List<Block> firstBlocks = CloneBlocks(first.thisAction.Blocks);
            List<Block> secondBlocks = CloneBlocks(second.thatAction.Blocks);
            foreach (Block b in firstBlocks)
            {
                if (b.TransferCmd is ReturnCmd)
                {
                    List<Block> bs = new List<Block>();
                    bs.Add(secondBlocks[0]);
                    List<string> ls = new List<string>();
                    ls.Add(secondBlocks[0].Label);
                    b.TransferCmd = new GotoCmd(Token.NoToken, ls, bs);
                }
            }
            List<Block> blocks = new List<Block>();
            blocks.AddRange(firstBlocks);
            blocks.AddRange(secondBlocks);
            List<Requires> requires = DisjointnessRequires(program, first, second);
            List<Ensures> ensures = new List<Ensures>();
            Expr transitionRelation = (new TransitionRelationComputation(program, first, second)).Compute();
            ensures.Add(new Ensures(false, transitionRelation));
            string checkerName = string.Format("CommutativityChecker_{0}_{1}", first.proc.Name, second.proc.Name);
            Procedure proc = new Procedure(Token.NoToken, checkerName, new List<TypeVariable>(), inputs, outputs, requires, new List<IdentifierExpr>(), ensures);
            Implementation impl = new Implementation(Token.NoToken, checkerName, new List<TypeVariable>(), inputs, outputs, locals, blocks);
            impl.Proc = proc;
            this.moverCheckerProgram.TopLevelDeclarations.Add(impl);
            this.moverCheckerProgram.TopLevelDeclarations.Add(proc);
        }

        private void CreateGatePreservationChecker(Program program, ActionInfo first, ActionInfo second)
        {
            Tuple<ActionInfo, ActionInfo> actionPair = new Tuple<ActionInfo, ActionInfo>(first, second);
            if (gatePreservationCheckerCache.Contains(actionPair))
                return;
            gatePreservationCheckerCache.Add(actionPair);

            List<Variable> inputs = new List<Variable>();
            inputs.AddRange(first.thisInParams);
            inputs.AddRange(second.thatInParams);
            List<Variable> outputs = new List<Variable>();
            outputs.AddRange(first.thisOutParams);
            outputs.AddRange(second.thatOutParams);
            List<Variable> locals = new List<Variable>();
            locals.AddRange(second.thatAction.LocVars);
            List<Block> secondBlocks = CloneBlocks(second.thatAction.Blocks);
            List<Requires> requires = DisjointnessRequires(program, first, second);
            List<Ensures> ensures = new List<Ensures>();
            foreach (AssertCmd assertCmd in first.thisGate)
            {
                requires.Add(new Requires(false, assertCmd.Expr));
                ensures.Add(new Ensures(false, assertCmd.Expr));
            }
            string checkerName = string.Format("GatePreservationChecker_{0}_{1}", first.proc.Name, second.proc.Name);
            Procedure proc = new Procedure(Token.NoToken, checkerName, new List<TypeVariable>(), inputs, outputs, requires, new List<IdentifierExpr>(), ensures);
            Implementation impl = new Implementation(Token.NoToken, checkerName, new List<TypeVariable>(), inputs, outputs, locals, secondBlocks);
            impl.Proc = proc;
            this.moverCheckerProgram.TopLevelDeclarations.Add(impl);
            this.moverCheckerProgram.TopLevelDeclarations.Add(proc);
        }

        private void CreateFailurePreservationChecker(Program program, ActionInfo first, ActionInfo second)
        {
            Tuple<ActionInfo, ActionInfo> actionPair = new Tuple<ActionInfo, ActionInfo>(first, second); 
            if (failurePreservationCheckerCache.Contains(actionPair))
                return;
            failurePreservationCheckerCache.Add(actionPair);

            List<Variable> inputs = new List<Variable>();
            inputs.AddRange(first.thisInParams);
            inputs.AddRange(second.thatInParams);

            Expr transitionRelation = (new TransitionRelationComputation(program, second)).Compute();
            Expr expr = Expr.True;
            foreach (AssertCmd assertCmd in first.thisGate)
            {
                expr = Expr.And(assertCmd.Expr, expr);
            }
            List<Requires> requires = DisjointnessRequires(program, first, second);
            requires.Add(new Requires(false, Expr.Not(expr)));
            foreach (AssertCmd assertCmd in second.thatGate)
            {
                requires.Add(new Requires(false, assertCmd.Expr));
            }
            
            Dictionary<Variable, Expr> map = new Dictionary<Variable, Expr>();
            Dictionary<Variable, Expr> oldMap = new Dictionary<Variable, Expr>();
            List<Variable> boundVars = new List<Variable>();
            foreach (Variable v in program.GlobalVariables())
            {
                BoundVariable bv = new BoundVariable(Token.NoToken, new TypedIdent(Token.NoToken, "post_" + v.Name, v.TypedIdent.Type));
                boundVars.Add(bv);
                map[v] = new IdentifierExpr(Token.NoToken, bv);
            }
            foreach (Variable v in second.thatOutParams)
            {
                {
                    BoundVariable bv = new BoundVariable(Token.NoToken, new TypedIdent(Token.NoToken, "post_" + v.Name, v.TypedIdent.Type));
                    boundVars.Add(bv);
                    map[v] = new IdentifierExpr(Token.NoToken, bv);
                }
                {
                    BoundVariable bv = new BoundVariable(Token.NoToken, new TypedIdent(Token.NoToken, "pre_" + v.Name, v.TypedIdent.Type));
                    boundVars.Add(bv);
                    oldMap[v] = new IdentifierExpr(Token.NoToken, bv);
                }
            }
            foreach (Variable v in second.thatAction.LocVars)
            {
                BoundVariable bv = new BoundVariable(Token.NoToken, new TypedIdent(Token.NoToken, "pre_" + v.Name, v.TypedIdent.Type));
                boundVars.Add(bv);
                oldMap[v] = new IdentifierExpr(Token.NoToken, bv);
            }

            Expr ensuresExpr = Expr.And(transitionRelation, Expr.Not(expr));
            if (boundVars.Count > 0)
            {
                Substitution subst = Substituter.SubstitutionFromHashtable(map);
                Substitution oldSubst = Substituter.SubstitutionFromHashtable(oldMap);
                ensuresExpr = new ExistsExpr(Token.NoToken, boundVars, (Expr)new MySubstituter(subst, oldSubst).Visit(ensuresExpr));
            }
            List<Ensures> ensures = new List<Ensures>();
            ensures.Add(new Ensures(false, ensuresExpr));
            List<Block> blocks = new List<Block>();
            blocks.Add(new Block(Token.NoToken, "L", new List<Cmd>(), new ReturnCmd(Token.NoToken)));
            string checkerName = string.Format("FailurePreservationChecker_{0}_{1}", first.proc.Name, second.proc.Name);
            Procedure proc = new Procedure(Token.NoToken, checkerName, new List<TypeVariable>(), inputs, new List<Variable>(), requires, new List<IdentifierExpr>(), ensures);
            Implementation impl = new Implementation(Token.NoToken, checkerName, new List<TypeVariable>(), inputs, new List<Variable>(), new List<Variable>(), blocks);
            impl.Proc = proc;
            this.moverCheckerProgram.TopLevelDeclarations.Add(impl);
            this.moverCheckerProgram.TopLevelDeclarations.Add(proc);
        }
    }
}
